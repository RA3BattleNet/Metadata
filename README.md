# 红警3战网元数据

本仓库存储战网客户端所需的**应用 / Mod 版本、清单引用、新闻与资源索引**，经核心构建后由 Cloudflare Pages 发布。

更完整的产品决策见 [PLAN.md](./PLAN.md)。

## 发布契约（Desktop 默认消费）

| 项 | 说明 |
|---|---|
| 默认 BaseUrl | `https://metadata.ra3battle.net`（占位，部署后替换为真实 Pages 域名） |
| 数据入口 | `GET {BaseUrl}/metadata.xml` — **已展平**的业务 XML（无 `Include`） |
| 资源 | `{BaseUrl}/{相对路径}` — Manifest / 图片 / Markdown 等**独立文件** |
| 版本字段 | 根节点属性 `SchemaVersion`、`ContentRevision` |
| 资源引用 | XML 内一律 **ID 引用**（如 `<Manifest>manifest-1.5.2.0</Manifest>`），再解析登记节点上的 `Source`/`Url` |
| Manifest 归属 | **Updater 生成**清单文件；本仓只登记 ID 并引用。Updater 格式改造见路线图 |

### 核心构建 vs Imaging（WebP）

| 路径 | 做什么 | 何时用 |
|---|---|---|
| **核心构建** `build` | 校验、变量、XML 展平、复制资源（**纯 managed，无 SkiaSharp**） | 本地开发、测试、Desktop 调试默认 |
| **Imaging** `build:webp` | 将 Output 内本地图转 WebP 并改写 `Source` | **仅发布**（显式开启） |

```bash
# 本地默认：只要核心产物
npm run build
# 或
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output

# 正式发布：核心 + WebP
npm run build:release
# 或（CF / 装 dotnet 的环境）
bash build.sh --webp
```

- Cloudflare：`wrangler.toml` 的 `[build].command = "bash build.sh --webp"`
- **没有** `RUN_STAGE_B`：默认不转 WebP，发布用 `--webp` / `build:release` 正向开启
- 本地与线上业务树同形；线上图片扩展名可能为 `.webp`，客户端按节点 `Source` 读，勿写死 `.png`

### 库 API

```csharp
// 核心构建（本期 Build 仅支持本地 path；远程源构建延后）
MetadataBuilder.Build(sourceDir, outputDir, schemaVersion: "1.0", contentRevision: "git-sha");

// 解析已展平产物（path 或 http(s) URL）
var doc = MetadataBuilder.Load(pathOrUrl);
var app = doc.Catalog().Application("RA3BattleNet");
```

主库 **无 SkiaSharp**；正式客户端可只引用 `Ra3.BattleNet.Metadata` 做解析与本地核心构建。

### 硬失败

下列情况核心构建 **非 0 退出**并清理半残输出：循环 Include、缺失资源、断开的 ID 引用、未替换的 `${...}`、校验失败。

## 本地命令

```bash
dotnet test Metadata.sln
npm run build              # 核心
npm run build:webp         # 仅 Imaging（需已有 Output）
npm run build:release      # 核心 + Imaging
npm run preview            # 核心后 pages dev
npm run deploy             # release + wrangler pages deploy
```

## 示例数据

- Application：`RA3BattleNet`
- Mod：`Corona`  
其余内容可自行补充；正式 Manifest Hash 应以 Updater 生成为准。

## Desktop 下期对接（本期不改 Desktop 仓）

1. 用户默认 `BaseUrl` 指向 Cloudflare Pages 展平产物。  
2. 开发者调试页：选择本地 Metadata 仓库路径 → 调用核心 `MetadataBuilder.Build` → `Load` 缓存目录中的 `metadata.xml`。  
3. 与线上同一套解析逻辑，仅换内容来源（本地一般不跑 Imaging）。

## 测试

- 框架：**MSTest**  
- **禁止**再引入 xunit 系包  
- 覆盖：展平、硬失败、示例核心构建成功路径  

## Include 与 public/private

源树可用 `Include`/`Includes` 分模块维护。`Type=public|private` **仅影响构建期解析**；发布物中无 Include，业务节点合并进单一 `metadata.xml`。

### 变量

| 语法 | 含义 |
|---|---|
| `${TIMESTAMP}` | UTC 时间 |
| `${ENV:NAME}` | 环境变量 |
| `${MD5:}` / `${MD5:rel}` | 当前文件或相对文件 MD5；Markdown `Hash="${MD5::}"` 按 `Source` 资源计算 |
| `${META:...}` / `${this:...}` | 树内/容器 Defines 引用 |

## 目录

```
Metadata/                         源数据
Ra3.BattleNet.Metadata/           纯 managed 库 + 核心 CLI
Ra3.BattleNet.Metadata.Imaging/   发布用 WebP（SkiaSharp）
Ra3.BattleNet.Metadata.Tests/     MSTest
build.sh                          CF：默认核心；--webp 加 Imaging
PLAN.md                           详细计划与决策记录
```
