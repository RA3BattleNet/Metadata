# 红警3战网元数据

本仓库是战网客户端的元数据仓库，承担两件事：**数据仓库**（作者提交源树，CI 构建后发布静态文件）+ **解析库**（`Ra3.BattleNet.Metadata`，客户端经 URL/本地入口读取发布物）。

- **写数据（新增/修改 Mod、应用、新闻）**：见 [AGENTS.md「数据贡献者指南」](./AGENTS.md) —— 5 步快速开始、三条规则、模板、提交前自检都在那里

## 发布物

静态文件树，不是 RPC：

- `{BaseUrl}/metadata.xml` —— 展平后的业务数据（**无 Include**）
- `{BaseUrl}/{相对路径}` —— 资源（图片/Markdown/叶子 Manifest），路径来自登记节点的 `Source`
- 根属性：`SchemaVersion`（契约版本）、`ContentRevision`（内容修订，用于缓存判断）

## 使用方如何解析（Desktop 视角）

1. 配置 `BaseUrl`（生产 = CDN；调试 = 本地缓存目录）。
2. `MetadataBuilder.Load(url|path)` → `Catalog` / `Applications` / `Mods`。
3. `SchemaVersion` 不兼容 → 提示升级客户端；`ContentRevision` 变化 → 整树刷新。
4. Package.Manifest / Icon / Changelog / Post.Content 等一律是**限定 ID**（`路径前缀:localId`），在展平树中找同 ID 的登记节点。
5. 资源 = `BaseUrl` + 登记节点 `Source`；图片扩展名以 `Source` 为准（发布后可能是 `.webp`）。
6. 叶子 Manifest（含 File 表）用 `Source` 再拉一次并解析——File 表不在 `metadata.xml` 内。

**开发调试 = 同一套解析**：`MetadataBuilder.Build(本地源仓, 缓存目录)` → `Load(cache/metadata.xml)`。

```csharp
// 生产：URL；开发：本地展平文件
var doc = MetadataBuilder.Load("https://metadata.ra3battle.net/metadata.xml");

var app = doc.Catalog().Application("RA3BattleNet");
var corona = doc.Mods().Single(m => m.Id == "Corona");
var iconId = corona.Icon;                       // 限定 ID，如 mods/corona/corona:corona-icon-64px
var reg = doc.GetAllElements("Image").First(i => i.Get("ID") == iconId);
var relative = reg.Get("Source");               // 拼 BaseUrl/BasePath 取文件
```

核心构建（作者/CI/Desktop 调试）：

```csharp
MetadataBuilder.Build(sourceDir, outputDir, schemaVersion: "1.0", contentRevision: gitSha);
```

主库**无 SkiaSharp**；WebP 仅发布期 `Ra3.BattleNet.Metadata.Imaging`。

### 校验与失败

核心构建**硬失败**（非 0、清理半残输出）：源树 XSD、发布物 XSD、循环 Include、缺资源、断 ID、残留 `${...}`。

## 模块属性清单

| 成员 | 位置 | 说明 |
|---|---|---|
| `SchemaVersion` / `ContentRevision` | 根属性 | 契约版本 / 构建注入修订 |
| `Application` | 子元素 | `@ID`、`Version`、`Packages`、`Posts` |
| `Mod` | 子元素 | `@ID`、`CurrentVersion`、`Icon`（Image ID）、`Style`、`Packages`、`Posts` |
| `Package` | 子元素 | `@Version`、`ReleaseDate`、`Changelogs`（`@Language`+Markdown ID）、`Manifest`（ID） |
| `Post` | 子元素 | `@DateTime`、`Titles`/`Contents`（`@Language` + Markdown ID） |
| `Image` / `Markdown` / `Manifest` | 登记节点 | `@ID`（限定 ID）、`@Source`（相对路径）；Image 可 `@Url` 外链；Manifest 为 stub |

`Style`（Logo/Controls/Background）与继承（`Base`/`InheritFrom`）细节：见 `Metadata/MetadataSchema.xsd` 与 AGENTS.md。
源树中登记 ID 一律短名，展平时自动限定为 `{路径前缀}:{localId}`；实体 ID 全局唯一，冲突即构建失败。

## 构建与发布

```bash
npm run build           # 核心构建（展平 + 校验）→ ./Output
npm run build:release   # 核心 + WebP（发布）
npm run deploy          # 构建 + Cloudflare Pages 部署
```

- **核心构建**（纯 managed）：XSD → 展平 → 变量 → 复制被引用资源 → 语义校验；`Output/` 只含 `metadata.xml` + 被引用资源 + `_redirects`
- **Imaging**：只转图并回传 MD5，不进主 NuGet；`--webp` 时展平后按图调用
- **Schema**：`Metadata/MetadataSchema.xsd`（源树）、`Metadata/MetadataPublishSchema.xsd`（发布物）
- **变量**：`${TIMESTAMP}` / `${ENV:NAME}` / `${MD5:}`

## 测试

- **MSTest** only（禁止 xunit）；`dotnet test Metadata.sln`
- 覆盖：展平、XSD 硬失败、ID/资源、防冲突、发布面、消费端 Catalog 解析

## 目录

```
Metadata/                         源数据 + XSD
Ra3.BattleNet.Metadata/           纯 managed 库 + 核心 CLI
Ra3.BattleNet.Metadata.Imaging/   发布 WebP
Ra3.BattleNet.Metadata.Tests/     MSTest
AGENTS.md                         Agent/贡献者/数据作者指引
```
