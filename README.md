# 红警3战网元数据

本仓库存储战网客户端所需的**应用 / Mod 版本、清单引用、新闻与资源索引**，经核心构建后由 Cloudflare Pages 发布。

- 产品决策：[PLAN.md](./PLAN.md)
- 贡献者 / Agent 指引：[AGENTS.md](./AGENTS.md)

---

## 使用方如何解析 Metadata（Desktop 视角）

发布物是**静态文件树**，不是 RPC。客户端只依赖：

1. 展平后的 **`metadata.xml`**（业务数据）
2. 资源文件（图片 / Markdown / 叶子 Manifest XML）
3. 可选：本库 `Ra3.BattleNet.Metadata`（C# 解析与查询）

### 1. 入口与版本

| 项 | 说明 |
|---|---|
| 默认 BaseUrl | `https://metadata.ra3battle.net`（占位，部署后替换） |
| 数据入口 | `{BaseUrl}/metadata.xml` |
| 根属性 | `SchemaVersion`（契约版本）、`ContentRevision`（内容修订） |
| 资源 | `{BaseUrl}/{相对路径}`，路径来自登记节点的 `Source` |

发布物 **无 `Include`**。源仓多文件仅供作者维护，使用方只读展平结果。

### 2. Desktop 推荐流程

```
┌─────────────────────────────────────────────────────────────┐
│  生产路径                                                    │
│  BaseUrl = CDN                                              │
│  Load(BaseUrl + "/metadata.xml")                            │
│       → Catalog / Applications / Mods                       │
│       → 按 ID 找 Image|Markdown|Manifest 登记节点            │
│       → 拼 BaseUrl + Source 下载资源                         │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│  开发调试路径                                                │
│  指定本地 Metadata 仓库 → MetadataBuilder.Build(src, cache) │
│       → Load(cache/metadata.xml)  （同一套解析）             │
│  本地一般不跑 Imaging；线上发布才 --webp                     │
└─────────────────────────────────────────────────────────────┘
```

**步骤（生产）：**

1. 配置 `BaseUrl`（用户默认 CDN；调试页可改本地/预览）。
2. `GET {BaseUrl}/metadata.xml`（或 `MetadataBuilder.Load(url|path)`）。
3. 读取 `SchemaVersion`：不兼容则提示升级客户端。
4. 读取 `ContentRevision`：与本地缓存比较，决定是否整树刷新。
5. `doc.Catalog().Application("RA3BattleNet")` / `doc.Mods()` 取业务实体。
6. 对 Package 的 `Manifest` 文本、Icon、Changelog、Post Content 等：**先当 ID**，在展平树中找同 ID 的登记节点。
7. 用登记节点的 `Source` 或 `Url` 取资源：
   - 本地文件：`Path.Combine(basePath, Source)`
   - 远端：`new Uri(new Uri(BaseUrl.TrimEnd('/') + "/"), Source)`
8. 叶子 Manifest XML（含 `<File Hash=...>` 表）用 `Source` 再拉一次并解析，**不要**假设 File 表在 `metadata.xml` 内。
9. 图片扩展名以节点 `Source` 为准（发布后可能是 `.webp`）。

**步骤（开发）：**

1. 调试页选择本地仓路径。
2. `MetadataBuilder.Build(repo/Metadata, cacheDir)`（核心构建，硬失败则展示错误）。
3. 后续与生产相同：`Load(cacheDir/metadata.xml)`，`basePath = cacheDir`。

### 3. 库 API 示例

```csharp
// 生产：URL
var doc = MetadataBuilder.Load("https://metadata.ra3battle.net/metadata.xml");

// 开发：本地展平文件
var doc = MetadataBuilder.Load(@"D:\cache\metadata.xml");

var app = doc.Catalog().Application("RA3BattleNet");
var corona = doc.Mods().Single(m => m.Id == "Corona");

// 解析 Manifest ID → 资源路径
var package = app!.Packages[0];
var reg = doc.GetAllElements("Manifest")
    .First(m => m.Get("ID") == package.ManifestId);
var relative = reg.Get("Source"); // e.g. apps/ra3battlenet/manifests/1.5.2.0.xml
// 再用 BaseUrl/BasePath + relative 取文件
```

核心构建（作者/CI/Desktop 调试编译）：

```csharp
MetadataBuilder.Build(sourceDir, outputDir, schemaVersion: "1.0", contentRevision: gitSha);
```

主库 **无 SkiaSharp**。WebP 仅 `Ra3.BattleNet.Metadata.Imaging` / `build.sh --webp`。

### 4. 校验与失败

核心构建 **硬失败**（非 0、清理半残输出）：

- 源树 XSD（`MetadataSchema.xsd`）
- 发布物 XSD（`MetadataPublishSchema.xsd`）
- 循环 Include、缺资源、断 ID、残留 `${...}`

---

## 模块属性清单

以下字段来自**真实示例 + 展平产物**，供 Desktop 建模对照。

### 根 `Metadata`（发布物）

| 成员 | 位置 | 说明 |
|---|---|---|
| `SchemaVersion` | 属性 | 契约版本，如 `1.0` |
| `ContentRevision` | 属性 | 构建注入的修订号 |
| `Tags/Commit` | 子元素 | 构建时间戳等 |
| 子节点 | 子元素 | `Application` / `Mod` / 登记用 `Image`·`Markdown`·`Manifest` |

源树另可有 `Includes/Include`（`Source`、`Type=public|private`），**发布物中不存在**。

### `Application`（应用，如战网客户端）

| 成员 | 说明 |
|---|---|
| `@ID` | 应用 ID，如 `RA3BattleNet` |
| `Version` | 当前版本号 |
| `Packages/Package` | 历史版本包列表 |
| `Posts/Post` | 新闻 |

### `Mod`

| 成员 | 说明 |
|---|---|
| `@ID` | Mod ID，如 `Corona` |
| `CurrentVersion` | 当前版本 |
| `Icon` | **ID 引用** → `Image` 登记节点 |
| `Style` | UI 样式（见下） |
| `Packages` / `Posts` | 同 Application |

### `Package`

| 成员 | 说明 |
|---|---|
| `@Version` | 包版本 |
| `ReleaseDate` | 发布日期字符串 |
| `Changelogs/Changelog` | `@Language` + 文本为 Markdown **ID** |
| `Manifest` | 文本为 Manifest **ID**（不是路径） |

### `Post`

| 成员 | 说明 |
|---|---|
| `@DateTime` | 时间 |
| `Titles/Title` | `@Language` + 标题文本 |
| `Contents/Content` | `@Language` + 文本为 Markdown **ID** |

### `Style`（Mod）

| 成员 | 说明 |
|---|---|
| `Logo` | `@Width` `@Height`，文本为 Image **ID** |
| `Controls/*` | 控件样式（如 `LaunchButton/BorderBrush`、`Label/FontSize`） |
| `Background` | `@Random`；子 `Image` 文本为 Image **ID**（不是登记节点） |

### 登记节点 `Image`

| 成员 | 说明 |
|---|---|
| `@ID` | 全局 ID |
| `@Source` | 相对路径（本地资源） |
| `@Url` | 外链（可与 Source 二选一） |

### 登记节点 `Markdown`

| 成员 | 说明 |
|---|---|
| `@ID` | 全局 ID |
| `@Source` | 相对路径（`.md`） |
| `@Hash` | 资源 MD5（构建时替换） |

### 登记节点 `Manifest`（**发布物 stub**）

| 成员 | 说明 |
|---|---|
| `@ID` | 全局 ID |
| `@Source` | 指向叶子清单 XML 的相对路径 |

### 叶子 Manifest 资源文件（独立 XML，Updater 生成）

根仍为 `Metadata`，内含完整：

| 成员 | 说明 |
|---|---|
| `Manifest/@ID` | 与 stub 一致 |
| `File/@Hash` | 文件哈希 |
| `File/FileName` | 文件名 |
| `File/RelativePath` | 相对路径 |
| `File/KindOf` | 类型标记 |
| `File/PatchInfo/Patch` | `@From` `@Method` `@Encryption` |
| `SubManifest` | 子清单引用 |

---

## 核心构建 vs Imaging

| 路径 | 做什么 | 何时用 |
|---|---|---|
| **核心构建** | XSD + 展平 + 变量 + 资源复制 | 本地、测试、Desktop 调试编译 |
| **Imaging** | WebP + 改写 Image Source | **仅发布**（显式） |

```bash
npm run build                 # 核心
npm run build:webp            # 仅 Imaging（需已有 Output）
npm run build:release         # 核心 + Imaging
bash build.sh --webp          # CF：装 dotnet + 核心 + Imaging
```

`wrangler.toml`：`command = "bash build.sh --webp"`。  
**无** `RUN_STAGE_B`：默认不转 WebP。

---

## Schema 文件

| 文件 | 用途 |
|---|---|
| `Metadata/MetadataSchema.xsd` | **源树**全部 `.xml` |
| `Metadata/MetadataPublishSchema.xsd` | **展平** `metadata.xml` |

构建顺序：源 XSD → 展平 → 变量 → 发布 XSD → 语义硬校验。

---

## 变量（源树 / 构建时）

| 语法 | 含义 |
|---|---|
| `${TIMESTAMP}` | UTC 时间 |
| `${ENV:NAME}` | 环境变量 |
| `${MD5:}` / `${MD5:rel}` | 文件 MD5；Markdown `Hash="${MD5::}"` 按 `Source` |
| `${META:...}` / `${this:...}` | 树内引用 |

---

## 本地命令

```bash
dotnet test Metadata.sln
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
npm run preview
npm run deploy
```

## 示例数据

- Application：`RA3BattleNet`
- Mod：`Corona`  
正式 Manifest Hash 以 **Updater** 生成为准。

## 测试

- **MSTest** only（禁止 xunit）
- 覆盖：展平、XSD 硬失败、ID/资源、消费端 Catalog 解析

## 目录

```
Metadata/                         源数据 + XSD
Ra3.BattleNet.Metadata/           纯 managed 库 + 核心 CLI
Ra3.BattleNet.Metadata.Imaging/   发布 WebP
Ra3.BattleNet.Metadata.Tests/     MSTest
AGENTS.md                         Agent/贡献者
PLAN.md                           计划与决策
```
