# 系统架构

本仓库同时承担两件事：存放 Mod 与应用的原始数据；提供客户端读取发布数据的 C# 解析库。两者之间靠一棵**静态文件树**连接，没有服务端接口。

```
Metadata/（源数据：XML、图片、Markdown、清单）
        │  dotnet run --project Ra3.BattleNet.Metadata -- build
        ▼
Output/（发布树：展平后的 metadata.xml + 被引用资源）
        │  上传 CDN / Cloudflare Pages / EdgeOne
        ▼
客户端：MetadataBuilder.Load(url|path) 或 MetadataClient（带磁盘缓存）
```

---

## 一、代码分层

| 层 | 位置 | 职责 | 谁用 |
|---|---|---|---|
| 数据契约层 | `Metadata/MetadataSchema.xsd`、`MetadataPublishSchema.xsd` | 约束作者写的源 XML 与最终发布物 | 构建器、编辑器 |
| 模型与查询层 | `Ra3.BattleNet.Metadata/Metadata.cs`、`MetadataQueryExtensions.cs`、`MetadataQueryModels.cs`、`MetadataSchema.cs`、`LanguageTag.cs`、`MetadataColor.cs`、`MetadataResourceUri.cs`、`ModCompatibility.cs` | XML 文档的内存模型、LINQ 查询、语言比较、颜色转换、地址拼装 | 构建器、客户端 |
| 构建工具层 | `MetadataBuilder.cs`、`MetadataFlattener.cs`、`MetadataInheritance.cs`、`SchemaValidator.cs`、`VariableResolver.cs`、`MarkdownValidator.cs`、`ImagePostProcessor.cs`、`ImagingInvoker.cs`、`Program.cs` | 展平、继承合并、校验、资源拷贝、调用 Imaging | 只有命令行构建 |
| 缓存层 | `Cache/MetadataClient.cs`、`MetadataDiskCache.cs`、`IMetadataTransfer.cs`、`HttpMetadataTransfer.cs`、结果记录 | 联网刷新、最后有效缓存、图片复用 | 客户端宿主 |
| 发版图片工具 | `Ra3.BattleNet.Metadata.Imaging/` | 把图片转成 WebP | 只有 `--webp` 构建 |

**依赖方向**：缓存层 → 模型与查询层；构建工具层 → 模型与查询层；模型与查询层不引用其他任何一层。`Ra3.BattleNet.Metadata` 是纯托管代码，不依赖 SkiaSharp 之类的图像库；图片转换只在发版时由独立项目执行。

---

## 二、构建流水线

`MetadataBuilder.Build(src, dst, schemaVersion, contentRevision, convertImages)` 按固定顺序执行，任何一步失败都会清空 `Output/` 后抛错，命令行进程返回 1：

1. **准备**：定位 `Metadata/metadata.xml` 作为唯一入口，删除并重建 `Output/`；找不到入口或源 XSD 直接失败。
2. **源侧校验**：用 `MetadataSchema.xsd` 校验整个源目录；再对独立清单做语义检查（哈希算法、字段组合、`Skudef` 引用）。
3. **展平**：展开 `Include`、按 `InheritFrom` 合并 `Base`、给资源短 ID 补上 `{路径前缀}:` 前缀、写入 `SchemaVersion` 与 `ContentRevision`。
4. **拷贝资源**：按登记节点的 `Source` 把图片、Markdown、独立清单复制到输出目录。独立清单不是原样照抄，它同样走一遍展平与变量解析。
5. **写盘与变量**：保存展平后的 `metadata.xml`，替换 `${TIMESTAMP}`、`${ENV:NAME}`，再检查是否残留 `${…}`。
6. **可选图片转换**：`--webp` 时调用 Imaging，把图片转成 WebP 并改写 `Source`。
7. **发布侧校验**：用 `MetadataPublishSchema.xsd` 校验产物，再做硬校验：ID 断链、资源缺失、显示名 `zh`/`en`、`Skudef` 与 `File` 的规则冲突、挂载角色、哈希格式等。

`Output/` 里只有发布面：展平后的 `metadata.xml`、被引用的资源文件、`_redirects`、`edgeone.json`、`editor.html`。源 XML、XSD、`Templates/` 不会进输出。

---

## 三、客户端读取路径

- **一次性读取**：`MetadataBuilder.Load(url|path)` 把整棵树读进内存，支持 LINQ 查询；适合命令行、测试和小工具。
- **带缓存的常驻读取**：`MetadataClient` 管理根清单、独立清单、图片三类资源，磁盘布局固定：

```
<cacheDir>/origin.json                         第一次打开时绑定的主发布基
<cacheDir>/metadata.xml                        主发布基的根清单
<cacheDir>/apps/...、<cacheDir>/mods/...       主发布基的资源，与发布树同名
<cacheDir>/.sources/<scheme>/<host_port>/<path>  其他发布基的正文
<cacheDir>/**/.etag、.uri、*.tmp               校验器、归属地址、写入中的临时文件
```

- **下载边界**：`IMetadataTransfer` 只负责把正文写到调用方给的本地路径；校验、落盘、替换缓存都由库自己完成。默认实现是 `HttpMetadataTransfer`，宿主可注入自己的实现（代理、平台网络栈）。
- **快照语义**：独立清单的身份只按调用时捕获的那份根快照解析，不会去翻更早或更晚的根。`GetCachedLeafAsync` 只读已有缓存、不发请求；`Refresh*` 才会联网。

具体 API 与字段含义见 [references/library-api.md](docs/references/library-api.md)。

---

## 四、必须守住的不变量

| 不变量 | 说明 | 谁在守 |
|---|---|---|
| 发布物只有静态文件 | 根 `metadata.xml` 里不允许残留 `Include` | 发布 XSD + 构建校验 |
| 完整 ID 由构建器生成 | 作者只写短名；手写冒号前缀直接报错 | 构建校验 |
| 资源路径只来自 `Source` | 客户端不推导文件名，也不猜扩展名 | 解析库 |
| `SchemaVersion` 保持 `1.0` | 只有客户端无法兼容的破坏性改动才提高版本 | 构建器 + 评审 |
| 构建失败不留半成品 | 任何校验失败都清空输出目录 | `MetadataBuilder.Build` |
| 缓存只按发布树布局 | 地址映射是纯函数，同一路径不会被两个地址互相覆盖 | `MetadataDiskCache` + `.uri` |

---

## 五、仓库结构

```text
Metadata/                         源数据（入口 metadata.xml，按 mods/ 与 apps/ 分目录）
Ra3.BattleNet.Metadata/           解析库 + 命令行构建工具
Ra3.BattleNet.Metadata.Imaging/   发版用的 WebP 转换工具
Ra3.BattleNet.Metadata.Tests/     MSTest 测试（构建、模型、缓存、兼容性）
scripts/verify-docs.mjs           知识库守卫（笔记格式、生成物新鲜度、链接）
build.sh                          线上构建入口，安装 .NET 后调用上面的命令行
.agents/                          全部研发文档与决策笔记
```

每个源文件的实际路径与行数见 [docs/generated/paths.md](docs/generated/paths.md)。
