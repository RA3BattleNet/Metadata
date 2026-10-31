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

## 二、一次性读取

```csharp
var doc = MetadataBuilder.Load("https://metadata.ra3battle.net/metadata.xml");
// 也可以是本地路径：MetadataBuilder.Load("./Output/metadata.xml")
```

内部按 `http`/`https`/`file` 分流；HTTP 路径带连接预算与有限重试，读不到就抛异常，不返回空文档。

```csharp
foreach (var mod in doc.Mods().Where(m => m.Version is not null).OrderBy(m => m.Id))
    Console.WriteLine($"{mod.Id}@{mod.Version}");

var app = doc.Catalog().Application("RA3BattleNet");   // 不区分大小写
var corona = doc.Catalog().Mod("Corona")!;

// 按版本取独立清单地址，再单独拉取
var leafUrl = MetadataResourceUri.Resolve("https://metadata.ra3battle.net/metadata.xml", corona.ManifestSource());
var manifest = MetadataBuilder.Load(leafUrl.AbsoluteUri).Find("Manifest")!.ToManifestEntry();

// 新闻与友情链接是原始节点，库不做强类型查询，也不替你筛语言
var posts = app!.Raw.Find("Posts");
var firstPost = posts?.Children.FirstOrDefault(c => c.Name == "Post");
var zhTitle = firstPost?.Find("Titles")?.Children
    .FirstOrDefault(c => c.Name == "Title" && LanguageTag.Matches(c.Get("Language"), "zh"))
    ?.Value;

var links = corona.Raw.Find("Links");

// 挂载计划：写了 Skudef 就按声明顺序和条件来，没写就走旧的 File@Mount 角色
var plan = ManifestMountPlanner.Build(
    manifest.Skudef,
    manifest.Files.Select(f => new ManifestMountFile(f.FileName, f.Mount, f.Language, f.Package)).ToList(),
    language: "en",
    packages: ["hd-shadow"],
    localConfigExists: _ => false);
```

### 读本地作者源文件（未发布）

```csharp
// 入口可以是带 Includes / Base / InheritFrom / 短 ID 的源文件；源根缺省为入口所在目录
var source = MetadataBuilder.LoadSource("./Metadata/mods/corona/corona.xml");
// 也可以显式给源根：ID 前缀与资源路径都相对它
var leaf = MetadataBuilder.LoadSource("./Metadata/mods/corona/manifests/3.258.xml", "./Metadata/mods/corona");
```

`LoadSource` 在内存里展平 Include、合并继承、限定登记 ID，返回普通 `Metadata` 树：不写任何文件，不替换 `${TIMESTAMP}` / `${ENV:...}`，不做 XSD 与跨实体 `Compatibility` 校验（单模组没有整棵目录），也不要求其他 Mod 存在。

- 源根缺省是入口文件所在目录；登记 ID 的前缀同样是文件相对源根、去 `.xml` 的路径，与构建一致。
- 资源 `Source` 改写为相对源根的路径：先按定义文件目录解释，不存在再按源根解释；两项都必须落在源根内，越界直接抛 `InvalidOperationException`，不会去探测、读取目标。
- 入口、Include 与资源路径越出源根同样在读取前拒绝；不跟随跨根链接。
- Include 进来的独立清单只留下登记 stub（ID 已限定、`Source` 相对源根）。`<Manifest ID Source>` 手写 stub 保留入口文件前缀，单独读叶子时前缀来自文件路径，两者可能不同，按 `ManifestRegistration(ID)` 精确查找前先用 `MetadataFlattener.LocalId` 对齐局部名。
- 需要发布物的严格校验（XSD、Compatibility、变量残留）时仍用 `Load` / `LoadFromFile`；它们拒绝任何含 `Include` 的源树。

已展平的内存文档可直接解析，不需要写临时文件：

```csharp
var doc = new XDocument(new XElement("Metadata", savedManifestElement));
var root = MetadataParser.Load(doc);   // 根必须是 Metadata，且不得含 Include / Includes / Module
```

### 导回作者源格式，以及单独解析清单

`LoadSource` 的反向操作和清单读取也在库里，宿主不再自己压短 ID、改 `Source` 或对齐前缀。

```csharp
// 整份 Mod：保留所有版本与 CurrentVersion；发布地址为空时保留相对 Source
var whole = MetadataSourceExporter.ExportWhole(root, "Corona", "https://metadata.ra3battle.net/metadata.xml");
// 单版本：只留这一版，并把它设成 CurrentVersion
var one = MetadataSourceExporter.ExportVersion(root, "Corona", "3.259");

// 本地声明里的版本清单：内联直接返回，stub 则按 Source 读叶子并按 LocalId 对齐
var manifest = MetadataSourceManifest.Resolve(localRoot, declarationPath, package.Raw);

// 独立清单节点或文本，不必再套假的 Metadata 根
var entry = MetadataParser.ParseManifest(manifestElement);
var same = MetadataParser.ParseManifest("<Manifest ID=\"m1\" ... />");

// 反方向：把解析出的节点还原成 XML，抄清单原文时用
var xml = MetadataXml.Write(manifest!);
```

`ExportWhole` / `ExportVersion` 只做纯转换：不写盘、不发请求、不改动传入的树。带 `Url` 的图片只保留外链，不猜 `Source`；返回的是语义等价的源格式，不恢复发布时丢掉的注释与拆分。

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
| `MetadataSchema.Current` / `IsCompatible(version)` | 契约版本与兼容判断；`Load` 不会自动调用 |
| `MetadataResourceUri.Resolve(baseUrl, source)` | 相对 `Source` 拼成绝对地址 |
| `MetadataColor.ToCss(value, format)` | 颜色转 CSS；ARGB 的 Alpha 移到末尾，非法值抛 `FormatException` |
| `ManifestMountPlanner.Build(...)` | 算出挂载指令序列 |
| `LanguageTag.Matches` / `Select` | 语言族比较 / 取第一条语言能对上的条目 |

## 四、带缓存的读取（`MetadataClient`）

```csharp
var client = new MetadataClient(cacheDirectory, TimeSpan.FromSeconds(30));
var root = await client.RefreshRootAsync(metadataUrl, ct);
var leafUri = MetadataResourceUri.Resolve(root.OriginUri!.AbsoluteUri, relativeSource);
var leaf = await client.GetLeafAsync(root, version, leafUri, ct);
var cached = await client.GetCachedLeafAsync(root, version, leafUri, ct);
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
- `GetCachedLeafAsync` 只取已有缓存，不发请求、不等待别人正在进行中的请求；缺失返回 `Unavailable`。
- `PreloadLeavesAsync` 的可用文档包含缓存回退，不代表本轮联网确认成功。
- 已登记图片命中完整缓存时直接返回，不发 HTTP，也不做后台校验。冷缓存只共享一次下载，失败后十分钟内普通读取不再请求；`RefreshImageAsync` 不受这个限制。
- `ImageRevision` 用旁挂摘要算虚拟地址，只有 32 位，碰撞时可能得到相同地址，不能当成绝不复用的证明；Markdown 正文里没有登记的图片不进这套机制。
- 关闭磁盘缓存时，`file://` 每次读当前源文件并返回 `Stale`；http(s) 不主动请求。

## 五、下载边界

`Cache/IMetadataTransfer.cs` 是唯一允许发网络请求的接口：

- 请求 `MetadataTransferRequest(Uri Source, string StagingPath, string? ETag, string? LastModified)`：`StagingPath` 是传输方在 200 时必须写完正文的本地路径，非 200 不得写。`ETag` / `LastModified` 用来发 `If-None-Match` / `If-Modified-Since`。
- 结果 `MetadataTransferResult(int StatusCode, string? ETag, string? LastModified, string? ContentType)`：真实状态码加服务端校验器。网络、超时、磁盘失败直接抛异常，不许用状态码敷衍。
- 默认实现 `HttpMetadataTransfer` 把正文直接下载到本地文件。宿主可以注入自己的实现（代理、平台网络栈），注入的实现归调用方释放。
- 下载名额默认 3（构造函数 `maxTransferConcurrency` 可调），根清单、独立清单、图片共用；本地 `file://` 不占名额。排队等名额的时间不计入单次请求超时。
- 预加载独立清单另有一层默认 4 的并发限制（`maxLeafConcurrency`），只约束同时请求的清单数量。
- 临时目录：传入 `stagingDirectory` 优先；开了磁盘缓存用 `<cacheDir>/.stage`；否则系统临时目录下的 `ra3-metadata-stage`。第一次真的需要联网时才创建。

传入 `transfer` 之后，构造函数里的 `httpClient` 不再参与任何请求。

## 六、地址与安全约束

只接受静态发布文件树。以下情况在写盘前抛 `ArgumentException`：带查询串、含跳转段或空段、含编码的斜杠或反斜杠、Windows 保留设备名、结尾空格或点、正文文件名带 `.etag`/`.tmp`/`.uri` 后缀、带用户信息或非 DNS／IPv4 主机、占用 `origin.json` 或 `.sources` 保留名。

`Apps/x.xml` 与 `apps/x.xml` 在 Windows 上是同一路径：一旦被某个地址占用，即使 `.etag` 被删，`.uri` 仍保留归属，正文不会被误读或覆盖。`SchemaVersion` 与客户端不兼容时不覆盖已有正文。
