# Metadata 改进与完善计划

> 状态：**本期代码已落地**（`dev` 分支）  
> 范围：本期仅 **Metadata 仓**；Desktop 联调另迭代  
> 优先级：**2 客户端契约 → 5 库产品化 → 1 示例内容**

---

## 0. 已锁定决策

| 项 | 结论 |
|---|---|
| 优先级 | **2 客户端契约 → 5 库产品化 → 1 示例内容** |
| 主消费端 | Desktop（本期不改 Desktop 仓） |
| 发布数据 | **XML 展平为单一 `metadata.xml`** |
| 发布资源 | Manifest / 图片 / Markdown **独立文件**，XML 内 **ID 引用** |
| public/private | 仅构建期可见性；线上无 `Include` |
| 用户默认 | Cloudflare Pages 展平产物（可配置默认 URL） |
| 开发者（下期 Desktop） | 调试页指本地仓 → 核心构建 → 读缓存 |
| 库形态 | **单一纯 managed 库**：解析 + 编译；支持 **path / URL** |
| 图片 | 库不做 WebP；**仅发布路径 Imaging**（`build.sh --webp`） |
| 校验 | **硬失败**，不产出半残发布物 |
| Manifest | **Updater 生成**，本仓只引用；Updater 改造 **记入路线图** |
| MVP 数据 | App `RA3BattleNet` + Mod `Corona` **示例**；其余自行补充 |
| 本期边界 | **仅 Metadata 仓** + Desktop 对接说明 |
| 测试框架 | **MSTest**；移除 xunit / xunit.v3 / xunit.runner 等 |

---

## 1. 目标架构

```
源仓 Metadata/          核心 build (库/CLI, 纯 managed)         Imaging (仅发布 --webp)
  xml 树 + 资源    →    校验 → 变量 → XML 展平 → Output/  →  WebP + 改写引用 → 部署 Pages
                              ↓
                     metadata.xml (数据, 含 SchemaVersion/ContentRevision)
                     + manifests/ images/ *.md ... (资源, ID 可解析)
```

### 1.1 发布契约（Desktop 将依赖）

- `GET {BaseUrl}/metadata.xml` — 展平后的业务数据树
- `{BaseUrl}/{相对路径}` — 资源文件
- 根节点字段：`SchemaVersion`、`ContentRevision`
- 资源通过 ID 引用（如 `<Manifest>manifest-1.5.2.0</Manifest>`、`<Icon>corona-icon-64px</Icon>`），**不写死域名**
- 展平树内存在对应 `<Manifest ID="...">` / `<Image ID="...">` 等登记节点，携带 `Source` / `Url` 等定位信息

### 1.2 数据 vs 资源

| 类型 | 处理 |
|---|---|
| **数据（XML）** | 构建期展平为**单一** `metadata.xml`（去掉 Include，合并业务树） |
| **资源** | Manifest / 图片 / Markdown 等**不进入**展平 XML 正文；独立存放，XML 只保留 ID/路径引用 |

### 1.3 本地调试 vs 线上（下期 Desktop，本期写清对接点）

- **最终用户**：默认读 Cloudflare Pages 展平产物（默认 BaseUrl）
- **开发者**：Desktop 调试页可选择指向**本地 Metadata 仓库** → 调用 Stage A 编译到缓存目录 → 与线上同形解析
- 默认：**手动「重新编译」**；可选目录监视自动编译
- 生产与调试共用同一套「展平 XML + 资源」解析逻辑，只换内容来源

---

## 2. 分期落地

### 阶段 A — 客户端契约与构建语义（优先级 2）

#### A1. 发布契约文档

- 默认 URL 占位、路径规则、ID 引用、版本字段
- 核心 build vs Imaging 产物差异（业务树同形，图片扩展名可能不同）
- 硬失败错误约定

#### A2. XML 展平（核心）

- 入口 `metadata.xml` 递归处理 Include
- public/private 按规则合并进树，输出**去掉 Include**
- **不内联** Manifest 文件表、图片字节、Markdown 文件正文
- 产出单一数据文件 + 资源目录

#### A3. 变量与校验硬失败

- 支持：`${TIMESTAMP}` / `${ENV:}` / `${MD5:}` / `${META:}` / `${this:}`
- 以下情况 **非 0 退出**，禁止发布：
  - XSD 失败
  - 循环 Include
  - 缺失资源文件
  - 断开的 Image / Markdown / Manifest ID
  - 未解析完的 `${...}` 残留
- 取消「Markdown 只警告仍成功」等软失败行为

#### A4. 输出布局

```
Output/
  metadata.xml          # 仅展平后的数据
  apps/...              # 资源（md / 图 / manifest xml 等）
  mods/...
```

- 推荐最终只复制**被引用**资源以减小发布面
- 示例阶段可先全量复制，再收紧

#### A5. 契约自测

- Include 展平、ID 可解析、硬失败用例
- CLI 对示例源 `build` → 断言 Output 形状
- 测试框架见 **§5**

### 阶段 B — 库产品化（优先级 5）

#### B1. 包边界

- `Ra3.BattleNet.Metadata`：类库（解析 + Stage A 构建）
- 可执行入口：同解决方案 CLI（`dotnet run` / 日后 `dotnet tool`）
- **主库无 SkiaSharp 等原生硬依赖**（WebP 仅 Imaging 项目）

#### B2. 公共 API（最小面）

```text
MetadataDocument.Load(path | url)                      // 解析已展平 XML
MetadataBuilder.Build(sourcePath | sourceUrl, outputDir) // Stage A
// 查询：Applications / Mods / 按 ID 取 Image|Manifest|Markdown 描述
// BasePath / BaseUrl 用于拼资源位置
```

- `Load(url)`：HTTP(S) 拉取展平后的 `metadata.xml`
- `Build(path)`：本地源仓
- `Build(url)`：远程源（zip 或静态源树）→ 缓存 → 同本地 Build  
  - 若首期仅实现 path，URL Build 预留 API，列入紧随迭代

#### B3. 版本字段

- `SchemaVersion`：契约版本（人工维护）
- `ContentRevision`：构建注入（UTC 时间或 git commit）

#### B4. 验收

- 无 SkiaSharp 引用下 `dotnet test` + `dotnet pack` 通过
- 示例：`Build(本地)` → `Load(Output/metadata.xml)` → 列出 App/Mod/资源 ID

### 阶段 C — 发布管线

#### C1. `build.sh`：默认核心，发布 opt-in WebP

1. 安装/使用 dotnet
2. **默认**：核心 build（纯 managed：校验、变量、XML 展平）
3. **`--webp` / `--publish`**（CF `wrangler` 使用）：Imaging WebP + 改写 Image Source
4. 任一步失败即 fail；**无** `RUN_STAGE_B` 环境变量

#### C2. Cloudflare Pages

- 部署 `Output/`
- 根路径 → `metadata.xml`（`_redirects` 或 Pages 配置）
- Worker `src/index.js`：删除无效假逻辑或明确仅资产透传

#### C3. 文档

- `npm run build` / `deploy` / 默认 URL 配置
- **Desktop 对接说明**（下期）：默认 BaseUrl；调试指本地仓；调 Stage A；Load 缓存

### 阶段 D — 示例数据（优先级 1，代码稳定后）

#### D1. 最小合法示例

- `Application ID=RA3BattleNet`：Version、Packages、Posts；Changelog/Manifest **ID 齐全**
- `Mod ID=Corona`：CurrentVersion、Icon、Packages、必要 Style/资源 ID
- 各 ID 对应真实存在的资源文件（占位 md / 小图 / 示意 manifest xml 即可）

#### D2. 自行补充（非本计划实现范围）

- 更多版本、Mod、文案、真实 Hash/清单
- 正式 Manifest **以 Updater 生成为准**

#### D3. 不进本期

- 地图下载、50+ Mod 填满
- Updater Manifest 格式改造（仅路线图）
- Desktop 调试页实现

---

## 3. 建议实施顺序（代码优先）

1. **测试栈切换为 MSTest**（§5），保证后续改动在统一测试框架上落地
2. **契约文档**（README 一节或本文件 §1 固化到用户文档）
3. **拆构建管线**：Stage A API；SkiaSharp 仅 Stage B
4. **实现 XML 展平 + 硬失败校验 + 版本字段**
5. **公共 Load/Build API + MSTest 测试**
6. **修 `build.sh` / wrangler / Worker**
7. **示例数据打到 Stage A 全绿**
8. **Desktop 对接说明写入 README**

---

## 4. 路线图（本期不做）

| 项 | 说明 |
|---|---|
| Desktop 调试页 | 选本地仓、触发 Build、切换默认 CF URL |
| Updater ↔ Manifest 生成/格式 | 生成器权威、禁止手改 Hash 等（**先保留在计划中**） |
| `Build(url)` 完整远程源 | 若首期只 path，下期补 |
| 仅复制被引用资源 | 发布瘦身 |
| Gitea + Preview URL | 本地 Stage A 已覆盖开发；远端预览可选 |
| 地图元数据、多 Mod 生产内容 | 内容扩展 |

---

## 5. 测试库：MSTest（强制）

### 5.1 决策

- **唯一测试框架：MSTest**
- **移除**：`xunit`、`xunit.v3`、`xunit.runner.visualstudio` 及全局 `Using Include="Xunit"`
- **保留/采用**：
  - `MSTest` 元包或 `MSTest.TestFramework` + `MSTest.TestAdapter`
  - `Microsoft.NET.Test.Sdk`
  - 断言：优先 MSTest 自带 `Assert`；若需更流畅断言可继续评估 FluentAssertions（**非 xunit 依赖**）
  - 覆盖率：`coverlet.collector` 可保留

### 5.2 迁移动作

1. 修改 `Ra3.BattleNet.Metadata.Tests.csproj`：去掉全部 xunit 包，改为 MSTest 包引用
2. 测试类：`[Fact]` → `[TestMethod]`，类上加 `[TestClass]`；构造/初始化改为 MSTest 生命周期（`[TestInitialize]` 等）
3. 删除 `Xunit` 全局 using；按需 `using Microsoft.VisualStudio.TestTools.UnitTesting;`
4. 全量 `dotnet test` 通过后再合入功能改动

### 5.3 约定

- 新建测试一律 MSTest，禁止再引入 xunit 系包
- 契约/硬失败/展平等核心路径必须有 MSTest 覆盖

---

## 6. 验收标准（本期完成时）

- [x] 测试项目为 **MSTest**，无 xunit 系依赖；`dotnet test` 全绿
- [x] 硬失败用例覆盖（schema / 缺文件 / 坏引用 / 变量残留 / 循环引用）
- [x] 类库无 SkiaSharp 等原生硬依赖
- [x] `metadata build --src Metadata --dst Output` 得到展平 `metadata.xml` + 资源
- [x] 展平 XML 无 Include；含 SchemaVersion/ContentRevision；资源 ID 可解析（Manifest Source 指向叶子清单文件）
- [x] CF 脚本含 Imaging WebP（`build.sh --webp`）；本地默认仅核心 build
- [x] 示例：RA3BattleNet + Corona 可通过完整核心 build
- [x] README：契约、默认 URL、Desktop 下期对接、Manifest 归属 Updater、测试用 MSTest

### 已实现说明（相对原文的偏差）

- Imaging 独立项目 `Ra3.BattleNet.Metadata.Imaging`（SkiaSharp）；默认不转 WebP，发布显式 `--webp` / `npm run build:release`
- `Load(url)` 已支持；`Build(url)` 远程源构建延后（见 §4）
- 用户可见面不再使用 StageA/StageB、`RUN_STAGE_B`

---

## 7. 风险与约定

- **本地 vs 线上图片扩展名**：客户端按 ID 读节点上的 `Source`，不写死 `.png`
- **展平体积**：只展平 XML **数据**；Manifest 等资源再大也独立文件
- **与「单文件」表述的关系**：展平的是数据 XML，不是把所有资源塞进一个文件
- **单库 + 纯 managed**：发布期 WebP 不得把原生依赖带回 Desktop 引用图
- **Updater**：Manifest 权威在 Updater；其格式/生成改造另项推进，本期 Metadata 只预留 ID 引用与校验

---

## 8. 决策记录（grill-me 摘要）

1. 优先级：契约 → 库 → 示例内容  
2. 第一消费端：Desktop  
3. 数据 XML 展平；资源外置 + ID 引用  
4. public/private 仅构建期  
5. 用户默认 CF URL；开发者本地仓 + 编译（Desktop 下期）  
6. 单库纯 managed；Parse/Build 支持 path 与 URL  
7. 本地：编译到缓存再读（与线上同形）  
8. URL 契约：单入口 + 相对资源 + SchemaVersion/ContentRevision  
9. WebP 仅发布 Imaging（opt-in `--webp`）；核心 build 与 Imaging 分离 
10. 校验硬失败  
11. Manifest 由 Updater 生成；Updater 改动进路线图  
12. MVP 示例：RA3BattleNet + Corona；代码优先，数据示例后自行补  
13. 本期边界：Metadata 库 + CLI + 示例 + 发布管线 + 对接文档  
14. **测试框架：MSTest，去掉 xunit**
