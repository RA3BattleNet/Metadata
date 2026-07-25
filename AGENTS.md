# AGENTS.md — Metadata 仓库指引

面向自动化 Agent 与人类贡献者。产品决策细节见 [PLAN.md](./PLAN.md)，使用方解析见 [README.md](./README.md)。

## 仓库职责

- **源数据**：`Metadata/**/*.xml` + 图片/Markdown/叶子 Manifest
- **核心库/CLI**：`Ra3.BattleNet.Metadata` — 展平、变量、XSD、语义校验、查询 API（**纯 managed，无 SkiaSharp**）；**Desktop NuGet 只引此包**
- **Imaging CLI**：`Ra3.BattleNet.Metadata.Imaging` — 编译期工具，WebP + 更新 Hash；**不进主 NuGet**；由 `build --webp` 进程调用
- **发布**：Cloudflare Pages 静态托管 `Output/`

## 常用命令

```bash
# 测试（MSTest）
dotnet test Metadata.sln

# 核心构建（默认；Desktop 本地调试同此）
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output

# 发布构建（核心 + WebP）
npm run build:release
# 或
bash build.sh --webp
```

- **不要**再引入 `RUN_STAGE_B` / `StageB` 控制面。
- 本地默认 **不** 跑 Imaging；CF：`wrangler.toml` → `bash build.sh --webp`。

## Schema

| 文件 | 校验对象 | 时机 |
|---|---|---|
| `Metadata/MetadataSchema.xsd` | 源树全部 `.xml` | 核心构建开始时硬失败 |
| `Metadata/MetadataPublishSchema.xsd` | 展平 `metadata.xml` | 变量替换后硬失败 |

实现：`SchemaValidator` + `MetadataBuilder.Build`。

改结构时：**同步改 XSD + README 属性清单 + 示例 + 测试**。

## 使用方（Desktop）解析要点

1. 只消费**展平** `metadata.xml` + 相对资源，不自己展开 Include。
2. `MetadataBuilder.Load(path|url)` → `Catalog` / `Applications` / `Mods`。
3. Package.Manifest / Icon / Changelog / Content 等是 **限定 ID**（`路径前缀:localId`），不是路径、也不是源树短名。
4. 登记节点 `Manifest[@Source]` 指向叶子清单 XML（含 File 表）。
5. 资源 URL = `BaseUrl` + `Source`；本地 = `BasePath` + `Source`。
6. 调试：本地 `Build` → 同一 `Load`；勿写死 `.png`。
7. 同名资源靠展平前缀隔离；勿在客户端再实现 public/private Include 语义。

完整步骤与属性表：README「使用方如何解析 Metadata」「模块属性清单」。

## 代码布局

```
Metadata/metadata.xml              入口
Metadata/**                        分模块 Include 源
Ra3.BattleNet.Metadata/
  MetadataBuilder.cs               核心 Build / Load
  MetadataFlattener.cs             Include 展平
  SchemaValidator.cs               XSD
  VariableResolver.cs              ${...}
  Metadata.cs / MetadataParser.cs  树与加载
  MetadataQueryExtensions.cs       Catalog/Mods/...
Ra3.BattleNet.Metadata.Imaging/    WebP
Ra3.BattleNet.Metadata.Tests/      MSTest
```

## 修改约束

- FAILFAST：校验失败即停，不留半残 `Output`。
- 测试只许 **MSTest**，禁止 xunit。
- 主库禁止加入 SkiaSharp / 原生图片依赖。
- Manifest **权威在 Updater**；本仓示例 Hash 可为占位。
- 中文注释与文档；代码标识符 ASCII。
- git 访问外网仓库时用代理 `http://localhost:7890`（项目惯例）。

## 不要做的事

- 在 Desktop 仓以外擅自改 Desktop UI（联调另迭代）。
- 把 File 表内联进发布 `metadata.xml` 的 Manifest stub。
- 默认开启 WebP 或恢复 `StageB` 命名作为 API。
