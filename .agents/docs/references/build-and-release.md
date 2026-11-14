# 构建、测试与发布

---

## 一、日常命令

```bash
dotnet test Metadata.sln                                            # 单元测试（MSTest）
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
npm run build           # 同上，走 package.json 脚本
npm run build:release   # 构建 + 图片转 WebP（等价 npm run build:webp）
npm run deploy          # 构建后发布到 Cloudflare Workers
npm run preview         # 本地用 wrangler 起静态服务看产物
```

命令行参数：`--src=`、`--dst=`、`--webp`。不带 `--dst` 时默认 `./Output`。构建没有任何版本参数：`SchemaVersion` 由作者写在源根 `metadata.xml` 的根节点上，构建也不追加修订标记，只解析 `${TIMESTAMP}` 等变量。

单格式保留 `build` 与 `export`。export 保留真实的 `--layout=flat|tree|mixed`、Mixed 选择性 Include 内联、显式 manifest 内联及资源/变量/图片处理；删除 `--id-format`，没有 legacy flavor。所有布局写同一 canonical path ID，不写格式 markers。无效选项清楚报错，不回退旧模式。Metadata 单格式与目录迁移已验证，Desktop 依赖集成待完成；证据见 [集成计划](../exec-plans/2026-11-07-composable-metadata.md)。

本地开发和调试默认不跑图片转换；只有线上发版才带 `--webp`。**不要在代码里再加 `RUN_STAGE_B` / `StageB` 之类的阶段开关**，流水线只有一条。

## 二、流水线

七个阶段的细节见 [../../ARCHITECTURE.md](../../ARCHITECTURE.md)。几条要点：

- 入口固定是 `<源目录>/metadata.xml`；找不到直接失败。
- 先校验源树（`MetadataSchema.xsd`），展平后再校验发布物（`MetadataPublishSchema.xsd`）。
- 所有 XML 读取共用 Reader；外部清单默认保持外部，构建导出时处理变量。没有严格发布读取/作者读取两条兼容流水线。
- `--webp` 在展平之后执行：`ImagePostProcessor` 调 Imaging 转图，再改写 XML 里的 `Source`。
- 失败一律清空 `Output/` 并返回 1，不留半成品。

## 三、发版用的 Imaging

`Ra3.BattleNet.Metadata.Imaging` 是独立项目，不打进 NuGet 主包，只在 `--webp` 时由 `ImagingInvoker` 以子进程调用。它只负责把图片转成 WebP；主库不引用任何图像库。

## 四、线上部署

| 目标 | 入口 | 说明 |
|---|---|---|
| EdgeOne | `.github/workflows/deploy.yml` | push 到 `main` 后执行 `bash build.sh --webp --content-revision=${{ github.sha }}`，再 `npx edgeone makers deploy ./Output` |
| Cloudflare Workers | `npm run deploy`（`wrangler.toml`） | 本地构建后执行 `wrangler deploy`，静态资源来自 `Output/` |

`build.sh` 自己下载 .NET 10 到 `./dotnet`，再调用命令行构建；`--content-revision=` 用来把提交标识写进根节点。

`_redirects`、`edgeone.json`、`editor.html` 如果存在于源目录，会原样复制到输出。

### Cloudflare Workers Git 构建

仓库只使用 Workers 静态资源，不使用 Pages，也不需要 JavaScript 透传入口。`wrangler.toml` 的 `[assets]` 指向 `./Output`，不配置 `[build]`，避免后台构建后 Wrangler 再构建一次。

| 后台设置 | 值 |
|---|---|
| 根目录 | 仓库根目录，不是内部的 `Metadata/` |
| 构建命令 | `bash build.sh --webp` |
| 生产部署命令 | `npx wrangler deploy` |
| 预览分支命令 | `npx wrangler preview` |
| 生产分支 | `main` |
| 预览分支 | 开启 Enable Preview Builds，非生产分支（如 `remix`）运行预览构建 |
| Node 环境变量 | 生产与预览都设置 `NODE_VERSION=22` |

`wrangler.toml` 必须包含 `[previews]`；本项目只有静态资源，因此该块为空，`[assets]` 与兼容日期仍放在顶层。不要把 `npm run deploy` 填进后台构建命令，它包含生产发布操作。

已接入 Builds 的旧 Worker 需要在 Settings > Builds 的 Set up Worker Previews 横幅中选择 Set up，再确认 Switch to Worker Previews。该切换不可逆；确认后预览命令必须调用 `npx wrangler preview`，不再使用 `versions upload`。生产分支保持 `main`，预览不替换生产部署。

先将含 `[previews]` 的 main 提交同步到已有功能分支，再触发分支构建。Preview 名称默认来自 Git 分支；成功后使用返回的 Preview URL 访问该分支最新部署，Unique Deployment URL 对应单次部署。无需为本项目导入生产变量、密钥或存储绑定。

本地先安装 .NET 10 SDK：`npm run preview` 构建后启动 Workers 本地服务，`npm run dev` 直接服务已有 `Output/`。`_redirects` 在 Workers 静态资源中继续负责将 `/` 重定向到 `/metadata.xml`。

参考：[构建分支与一次性切换](https://developers.cloudflare.com/workers/ci-cd/builds/build-branches/)、[Worker Previews 入门](https://developers.cloudflare.com/workers/previews/get-started/)、[Preview 配置](https://developers.cloudflare.com/workers/previews/configuration/)。

## 五、测试

`Ra3.BattleNet.Metadata.Tests` 使用 MSTest，禁止混入 xunit。覆盖范围：

- Reading/：Include、继承、路径 ID、引用优先级/永久歧义、HTTP/source-root 安全；SourceLoad/OpenMethod 本质用例归并，删除专测旧 API/mode 与固定 stopwatch PerformanceTests。
- Exporting/：Flat/Tree/Mixed、内外部清单和短 ID 作者写出；删除旧双 encoding 矩阵的一维，不机械改写所有 obsolete fixtures。
- Building/：Build（不再 StageA 命名）、Schema、ManifestValidation、Imaging、Inheritance；保留真实 Skudef/Mount 与领域校验。
- Cache/：完整 snapshot、JSON、ETag/原始字节配对、URI 安全、offline 和图片节奏；删除旧 flat-cache migration 测试，不重钉旧布局。
- Queries/：Catalog/LINQ、语言、颜色、样式和游戏兼容性。共享 TestData 原位置不变，不新增通用测试框架。父在集成 freeze 后移动目录，完整表见计划。

改构建器或缓存行为时，先补测试再改实现；测试失败不允许用重跑掩盖。

## 六、改完必须跑的三件事

```bash
dotnet test Metadata.sln
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
node scripts/verify-docs.mjs
```

前两条证明代码和数据没坏，第三条证明文档没腐烂。
