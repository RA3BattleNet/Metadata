# Agent Note: Metadata 解析库的运行期本地缓存

Status: proposed

## Problem

当前 [MetadataBuilder.cs](../../../../Ra3.BattleNet.Metadata/MetadataBuilder.cs#L169-L209) 每次加载 HTTP URL 都新建客户端，把正文写到临时文件后解析并清理；这不是持久缓存。Desktop 重启后仍要重新取得根目录和各版本叶子清单。网络失败会影响目录显示，但不应让本机已安装版本消失。

缓存应由 Metadata 解析库提供，而非在 Desktop 的 App/Mod 两个读取器分别实现。宿主决定缓存目录、日志、超时和启动时机；库不硬编码 RA3BattleNetData，不下载 Mod/Content/安装器二进制，也不管理本机安装版本。

本仓未发现已有决定笔记目录，因此新建本篇 proposed。总体要求见 [下载与安装总体指导](../../../../../Desktop/.agents/notes/proposed/architecture/2026-10-02-download-system-guidance.md)；本地安装记录见 [M2](../../../../../Desktop/.agents/notes/proposed/architecture/2026-10-02-local-installation-metadata.md)。

## Proposal

### 1. 边界

缓存内容分四类：

| 类型 | 刷新方式 | 失败后行为 |
|---|---|---|
| 根 Metadata XML | 每次启动一个有界条件请求 | 使用最后有效根；首次无缓存返回 Unavailable |
| 叶子文件清单 | 当前版本/被选择版本优先，其他清单低优先预取 | 对应 Package 标记资源未就绪，不能构造空请求 |
| Image/Markdown | 受界面需要驱动、去重和并发限制，可低优先预取 | 图片占位、说明不可用，不阻塞已安装版本 |
| Updater 目标清单 | Application 更新任务选择后按固定身份获取 | 未校验成功不开始自动整包安装 |

`File/Sources/Source` 指向的大型制品不是元数据缓存资源。HTTP/torrent 大文件获取仍属于下载系统；缓存层不因解析出某个 URL 就递归下载它。Markdown 内的任意超链接也不是自动抓取目标。

### 2. 拟议接口

```csharp
Task<CatalogLoadResult> OpenAsync(CacheOptions options, CancellationToken token);
Task<CatalogRefreshResult> RefreshAsync(CacheOptions options, CancellationToken token);
Task<CachedResource> ResolveResourceAsync(
    SnapshotLease snapshot, ResourceRef resource, CancellationToken token);
```

输入：入口 URL、宿主给出的绝对 CacheRoot、可注入 HttpClient/handler、时间/体积/并发上限、日志适配器。不得静默修改系统代理或证书策略。

输出：SnapshotId、根原文字节摘要、源入口 URL、取得时间、数据修订、Fresh/Stale/Unavailable、资源可用状态、只读文件路径/流以及租约。

- `OpenAsync` 只打开已验证的本地快照，必要时恢复缓存指针，不隐式无限等待网络。
- Desktop 启动先 Open，再单独调用一次 Refresh。首次无缓存可等待有界刷新；不阻塞本地安装列表加载。
- 同进程同入口共享一次刷新任务，App、Mod、Updater endpoints 不能各自重新拉根。
- 跨进程通过独占文件租约保护当前指针；下载对象可并行，但同对象合并请求。
- 现有 `MetadataBuilder.Load(pathOrUrl)` 保留；新缓存 API 不改变旧同步加载的语义，也不引入 Desktop 依赖。

### 3. 文件布局与身份

宿主传入 `RA3BattleNetData/Metadata/Remote`。库实际布局：

```text
current.xml
current.xml.pending
current.xml.previous
snapshots/<root-digest>/metadata.xml
snapshots/<root-digest>/resources.xml
objects/<sha256-of-resource-bytes>/payload
requests/<safe-request-key>.xml
staging/<attempt-id>/...
```

- 一个 CacheRoot 固定绑定一个规范化 OriginEntryUri 和请求配置范围，首次写入 origin.xml；后续入口/认证范围不匹配时拒绝复用该根，宿主需另选来源子目录。不能仅因两个入口根 XML 字节相同就复用资源映射，因为相对 Source 的含义依赖 origin。
- 根和对象保存服务器原始 XML/媒体字节，不为了本地路径而重写；原文字节摘要保持稳定。
- SnapshotId 由根原文 SHA-256 得到；`ContentRevision` 作为发布者标签，不能独自证明字节一致。
- HTTP 缓存验证器绑定完整请求身份，包括原始入口、最终重定向 URL及内容协商；ETag 不是跨镜像通用标识。
- 请求地址中的敏感参数不进入日志或可读索引。必要请求数据使用凭据引用或受限私有存储；UI 仅见脱敏地址。
- `resources.xml` 映射发布登记 ID、远端 URL、期望 hash/size、对象摘要、状态和最后错误；小文件映射也采用崩溃安全 generation 保存。
- 对象不可原地修改。同内容对象可复用普通文件读取，不能修改为另一个资源；不通过硬链接建立可写共享。

### 4. 刷新算法

1. 取得刷新租约，读取最后有效 current 及对应根。
2. 发送条件 GET：有已验证对象才携带 ETag/Last-Modified；没有对象不能仅凭 304 成功。
3. `304`：确认当前根和身份仍有效；若文件丢失或损坏，无条件重取一次。
4. `200`：流式写候选，限制正文长度；拒绝 HTML 挑战页、无效 XML、DTD/外部实体、非法 schema 和错误根。
5. 用发布 schema 与必要语义规则验证根、ID、登记引用；保持老发布契约兼容，不把 XSD 未知字段静默忽略视为兼容。
6. 将候选根作为新快照保存；优先解析当前 Package 所需资源。目录可切到“根有效、部分资源待取得”的快照，但必须逐 Package 暴露可用状态。
7. 当前指针采用 pending/current/previous 协议切换；中断时选择最高有效 generation，指针必须引用有效根。
8. 后台同步其余登记资源；任何下载完成都只能更新这个 SnapshotId 的映射，不能写入另一个新快照。
9. 失败保留旧快照和错误时间。返回 Stale 并让界面显示“使用缓存”，不能把空目录当正常刷新结果。

默认建议值：根请求总时限 15 秒、一次启动自动重试最多 2 次且退避，资源并发 4；根 XML 上限 8 MiB、叶子 XML 32 MiB、单媒体资源 32 MiB。这些是安全默认草案，需按现有真实发布物测量后固定，不依赖服务器 Content-Length 才限流。

### 5. 根和叶子一致性

根有效不等于其所有叶子都属于同一发布：

- 有期望摘要/size 的资源，下载后必须核对，错误对象不得用于该快照。
- 最佳发布契约是根登记携带叶子原文 SHA-256，或使用内容寻址、不可覆盖的资源 URL。
- 旧登记只有 URL 没有强摘要时，只能证明拿到了合法文件，不能证明跨资源原子发布。显示/浏览可兼容，标记 LegacyBinding。
- 新自动 Application 安装必须有目标版本与制品/Updater 清单的可验证绑定；缺绑定返回 `InsufficientManifestBinding`，不能“多拉一次根就当一致”。
- Mod 旧清单可在用户选中时冻结实际取得的合法原文和解析规格，其身份进入任务与本地安装记录；同一 PackageVersion 后来取得不同内容不能覆盖运行中的请求。
- 根刷新后叶子暂未发布完成：该 Package 等待/报错；不能把其他版本或上次同 URL 内容冒充新叶子。旧已安装版独立可用。

### 6. 资源地址解析

当前 [MetadataResourceUri.cs](../../../../Ra3.BattleNet.Metadata/MetadataResourceUri.cs) 对本地根会生成 file URI。缓存 API 因此必须保留 **OriginBaseUri**，所有发布 Source 都先按原远端入口解析，再找对应本地对象，不能用缓存目录当远端基准。

`CachedResource` 给调用方本地只读路径和来源身份。调用方不可自己拼 `cacheRoot + remoteSource`，否则会遇到 `../`、大小写碰撞、非法盘符和跨源资源覆盖。

允许 HTTP(S) 的显式登记来源；file URL 仅用于宿主指定的本地开发入口，不接受远端文档指向任意本机文件。重定向限制次数与 scheme，跨域不携带来源域的认证信息；不绕过 TLS 错误或浏览器挑战。

媒体 URL 型登记也通过资源解析器读取，避免已缓存根却仍让 UI 每次直连。未被当前 UI 使用的历史媒体可懒加载；历史 Package 叶子可低优先预取，不是所有版本安装包预取。

### 7. 离线、本地安装与租约

- 本地安装所需清单保存在 Installed 记录中，不依赖远端缓存永久保留。
- 本地记录不含样式；展示可关联当前/旧远端缓存，完全没有展示资料时使用 ID 占位。
- 安装/下载任务冻结必要规格和目标清单副本后，可释放大部分目录租约；若仍引用缓存对象，必须保留明确租约。
- 缓存清理只处理没有 current/previous、读者租约或活动任务引用的对象；先标记候选再回收，不能从远端下架直接推导应卸载本地版本。
- 默认保留 current、previous 以及活动租约；容量限制达到时优先回收无引用媒体。空间不足时保留有效根并报错，不先破坏唯一可用缓存。
- 刷新与清理失败不修改 Installed，不改下载队列终态，不修改 Updater 基线。

### 8. 文件与开发步骤

拟议新增 CacheOptions、MetadataCache、CatalogSnapshot、ResourceResolver、CacheStore 等类型；放在现有 Metadata 解析项目或其内部 Cache 子目录，保持 managed，不引入 Avalonia/NSIS。

1. 定义结果/错误模型和可注入 HTTP/存储/时钟；建立离线 fixture。
2. 实现根 current/pending/previous 恢复及有界条件刷新。
3. 实现保留 origin 的资源解析和内容对象缓存。
4. 实现资源摘要校验、快照租约、预取优先级。
5. Desktop 统一 AppMetadataReader、ModCatalogSource 读取；不保留多套进程内"各自根"。
   【实施现状】这两条已接到缓存（`MetadataRuntime`，缓存根 `RA3BattleNetData/Metadata/Remote`）：
   Open 本地快照后后台刷新，叶子清单每次向服务端核对。
   **Update endpoint 暂不接管** —— 它服务的是增量更新那条线，本轮明确不动，
   因此"一个进程只有一份根"目前只在 App/Mod 两条线上成立。
6. 发布侧逐步增加叶子强摘要和不可覆盖资源契约；兼容客户端先发，再发布新 schema。

## Alternatives considered

- **每个 Desktop 读取器自管缓存**：局部修改少，但重复联网、缓存不一致和路径规则会分叉；库提供通用缓存，宿主只管理生命周期。
- **把根下载到一个固定文件并直接覆盖**：易理解，但请求中断会失去唯一有效目录；使用不可变对象和可恢复指针。
- **下载所有大型 File 来源实现完整镜像**：离线能力强，但启动可能下载所有 Mod 和安装器；缓存仅覆盖控制/展示资源，大型下载必须是独立用户或更新任务。
- **把展示数据复制进本地安装 XML**：离线显示方便，但与用户要求相反且会制造陈旧样式；展示缓存与安装定义分离。

## Acceptance criteria

- MC-01：根 200 后下次 304 复用；没有本地对象的 304 只允许无条件重取一次，不假成功。
- MC-02：断网、超时、HTML 200、截断 XML、错误 schema 不覆盖最后有效根。
- MC-03：根更新、叶子旧字节/缺失/摘要错误时 Package 不可新装，但已有安装仍可启动。
- MC-04：本地根路径仍按原远端 origin 解析相对 Source，跨域/路径逃逸被拒绝；两个入口提供相同根字节但不同相对资源时，不允许共用同一 CacheRoot 映射。
- MC-05：App/Mod 同时请求只触发一个启动刷新；取消一个读者不破坏其他租约。
- MC-06：媒体失败仅占位；首次完全离线返回 Unavailable 与空远端目录的区别可观测。
- MC-07：快照切换和清理的每个故障点都能保留一份有效目录，活动对象不被回收。
- MC-08：刷新从不触发 File/Sources 大型制品下载，从不改本地安装记录。

## Risks

- 历史发布物缺少叶子摘要是公开限制，必须在结果模型中表达，不能用本地计算 SHA-256 假装获得了发布者预期值。
- 全部媒体预取会扩大流量和空间；默认按需、低优先预取，目录完整与图片完整不是同一成功条件。
- 缓存 root 同形不能保证所有业务读者用对 origin，必须迁移统一入口并禁止直接拼 URL。
- 本方案不要求立即改变发布数据；强绑定缺失时应禁用对应自动安装能力，而不是悄悄放宽安全判据。
