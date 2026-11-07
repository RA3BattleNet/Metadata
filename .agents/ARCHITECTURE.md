# 系统架构

本仓库同时承担两件事：存放 Mod 与应用的原始数据；提供客户端读取发布数据的 C# 解析库。两者之间靠一棵**静态文件树**连接，没有服务端接口。

```
Metadata/（源数据：XML、图片、Markdown、清单）
        │  dotnet run --project Ra3.BattleNet.Metadata -- build
        ▼
Output/（发布树：展平后的 metadata.xml + 被引用资源）
        │  上传 CDN / Cloudflare Pages / EdgeOne
        ▼
客户端：MetadataReader.Read(url|path).Root 或 MetadataClient（带磁盘缓存）
```

当前单格式决定见 [集成计划](docs/exec-plans/2026-11-07-composable-metadata.md) / [single-format 决定](notes/implemented/simplification/2026-11-07-single-metadata-format.md)，Metadata 已完成 fullsuite/Build/layout/HTTP/offline 验证，Desktop 依赖交付与验证待完成。用户明确取消旧兼容：只有 Reader 和 canonical path ID，Flat/Tree/Mixed 仅改变组织，不保留 legacy publish、旧读取 API 或格式检测。旧 composable note 保留历史理由和证据，不代表当前验证。

---

## 一、代码分层

| 层 | 位置 | 职责 | 谁用 |
|---|---|---|---|
| 数据契约层 | `Metadata/MetadataSchema.xsd`、`MetadataPublishSchema.xsd` | 约束作者写的源 XML 与最终发布物 | 构建器、编辑器 |
| 模型与查询层 | `Ra3.BattleNet.Metadata/Metadata.cs`、`MetadataQueryExtensions.cs`、`MetadataQueryModels.cs`、`MetadataSchema.cs`、`LanguageTag.cs`、`MetadataColor.cs`、`MetadataResourceUri.cs`、`ModCompatibility.cs` | XML 文档的内存模型、LINQ 查询、语言比较、颜色转换、地址拼装 | 构建器、客户端 |
| 读取与组合层 | `Reading/`：Reader、Id、DocumentInfo、SourceOptions、XML sources、SourceManifest | 单一读取、继承、引用解析、文档相对 Source 与捕获图；不写盘 | 构建、导出、缓存、客户端 |
| 导出层 | `Exporting/`：Exporter、Options、SourceExporter、Xml | 单格式 Flat/Tree/Mixed、资源布局和显式短 ID 作者写出 | 构建、工具、Desktop |
| 构建工具层 | `Building/`：Builder、Inheritance、SchemaValidator、VariableResolver、MarkdownValidator、ImagePostProcessor、ImagingInvoker；根 Program | schema/领域校验、变量和可选图片转换；不保留读取 API | 命令行构建 |
| 缓存层 | `Cache/MetadataClient.cs`、`MetadataDiskCache.cs`、`IMetadataTransfer.cs`、`HttpMetadataTransfer.cs`、结果记录 | 联网刷新、最后有效缓存、图片复用 | 客户端宿主 |
| 发版图片工具 | `Ra3.BattleNet.Metadata.Imaging/` | 把图片转成 WebP | 只有 `--webp` 构建 |

**依赖方向**：缓存获取 XML 后交给 Reading；Building 使用 Reading 和 Exporting，Exporting 使用捕获图；模型/query/core 不依赖这些层。父在 owner freeze 后按责任目录搬文件，命名空间不变。主库纯托管，不依赖 SkiaSharp；图片转换由独立项目执行。

---

## 二、构建流水线

`MetadataBuilder.Build(src, dst, schemaVersion, contentRevision, convertImages)` 按固定顺序执行，任何一步失败都会清空 `Output/` 后抛错，命令行进程返回 1：

1. **准备**：定位 `Metadata/metadata.xml` 作为唯一入口，删除并重建 `Output/`；找不到入口或源 XSD 直接失败。
2. **源侧校验**：用 `MetadataSchema.xsd` 校验整个源目录；再对独立清单做语义检查（哈希算法、字段组合、`Skudef` 引用）。
3. **读取**：Reader 展开 Include、合并继承、将短资源 ID 限定为 `{scope}/{local}`；Source 相对定义文档解释，SourceRoot 只负责限定/containment。读取不覆盖 schema/revision。
4. **导出**：普通 Export 写平面发布根和默认外部清单/资源，显式设置 SchemaVersion/ContentRevision；不存在 ExportLegacy 或第二套资源拷贝。
5. **写盘与变量**：保存展平后的 `metadata.xml`，替换 `${TIMESTAMP}`、`${ENV:NAME}`，再检查是否残留 `${…}`。
6. **可选图片转换**：`--webp` 时调用 Imaging，把图片转成 WebP 并改写 `Source`。
7. **发布侧校验**：用 `MetadataPublishSchema.xsd` 校验产物，再做硬校验：ID 断链、资源缺失、显示名 `zh`/`en`、`Skudef` 与 `File` 的规则冲突、挂载角色、哈希格式等。

`Output/` 里只有发布面：展平后的 `metadata.xml`、被引用的资源文件、`_redirects`、`edgeone.json`、`editor.html`。源 XML、XSD、`Templates/` 不会进输出。

---

## 三、客户端读取路径

- **一次性读取**：`MetadataReader.Read(url|path).Root` 是唯一文档读取入口。Builder.Load/LoadSource/Open、Flattener 和兼容 Detector 不保留；Parser 只投影 XML。
- **带缓存的常驻读取**：`MetadataClient` 管理根清单、独立清单、图片三类资源，磁盘布局固定：

```
<cacheDir>/origin.json                         第一次打开时绑定的主发布基
<cacheDir>/metadata.xml                        主发布基的根清单
<cacheDir>/apps/...、<cacheDir>/mods/...       主发布基的资源，与发布树同名
<cacheDir>/.sources/<scheme>/<host_port>/<path>  其他发布基的正文
<cacheDir>/**/.etag、.uri、*.tmp               校验器、归属地址、写入中的临时文件
```

- **下载边界**：`IMetadataTransfer` 只负责把正文写到调用方给的本地路径；校验、落盘、替换缓存都由库自己完成。默认实现是 `HttpMetadataTransfer`，宿主可注入自己的实现（代理、平台网络栈）。
- **快照语义**：包清单只按捕获根快照和 PackageEntry 精确解析，不翻更早/更晚根，也不从 URI 猜 source-root。Get/GetCached/RefreshPackageAsync 保留；URI-only Get/GetCached/RefreshLeafAsync 及其匹配 glue 删除。外部 GetCachedPackageAsync 只读缓存，内联 body 继承快照状态；完整 normalized snapshot 与 raw XML/validators 分开保存。

具体 API 与字段含义见 [references/library-api.md](docs/references/library-api.md)。

---

## 四、必须守住的不变量

| 不变量 | 说明 | 谁在守 |
|---|---|---|
| 发布物只有静态文件 | Build 发布根没有 Include；Tree/Mixed export 保留明确 Include 组织 | 导出 + 发布校验 |
| 单一资源身份 | 短资源 ID 总是限定，canonical path ID 校验后保留；冒号格式不支持；业务 ID 不变 | Reading/MetadataId |
| 文档相对资源地址 | Source 相对定义文档，Root 中相对入口；SourceRoot 不提供回退；Image.Url 优先 | Reader + 路径安全 |
| 读取不伪造版本 | schema/revision 保留原声明，Build/export 显式设置；无 IdFormat/SourceBase markers | Reader + Exporter |
| 构建失败不留半成品 | 任何校验失败都清空输出目录 | `MetadataBuilder.Build` |
| 缓存只按发布树布局 | 地址映射是纯函数，同一路径不会被两个地址互相覆盖 | `MetadataDiskCache` + `.uri` |

---

## 五、仓库结构

```text
Metadata/                         源数据（入口 metadata.xml，按 mods/ 与 apps/ 分目录）
Ra3.BattleNet.Metadata/           解析库 + 命令行构建工具
Ra3.BattleNet.Metadata.Imaging/   发版用的 WebP 转换工具
Ra3.BattleNet.Metadata.Tests/     MSTest：Reading/、Exporting/、Building/、Cache/、Queries/；TestData 保持位置
scripts/verify-docs.mjs           知识库守卫（笔记格式、生成物新鲜度、链接）
build.sh                          线上构建入口，安装 .NET 后调用上面的命令行
.agents/                          全部研发文档与决策笔记
```

每个源文件的实际路径与行数见 [docs/generated/paths.md](docs/generated/paths.md)。
