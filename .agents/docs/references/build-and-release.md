# 构建、测试与发布

---

## 一、日常命令

```bash
dotnet test Metadata.sln                                            # 单元测试（MSTest）
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
npm run build           # 同上，走 package.json 脚本
npm run build:release   # 构建 + 图片转 WebP（等价 npm run build:webp）
npm run deploy          # 构建后发布到 Cloudflare Pages
npm run preview         # 本地用 wrangler 起静态服务看产物
```

命令行参数：`--src=`、`--dst=`、`--webp`、`--schema-version=`、`--content-revision=`。不带 `--dst` 时默认 `./Output`。

本地开发和调试默认不跑图片转换；只有线上发版才带 `--webp`。**不要在代码里再加 `RUN_STAGE_B` / `StageB` 之类的阶段开关**，流水线只有一条。

## 二、流水线

七个阶段的细节见 [../../ARCHITECTURE.md](../../ARCHITECTURE.md)。几条要点：

- 入口固定是 `<源目录>/metadata.xml`；找不到直接失败。
- 先校验源树（`MetadataSchema.xsd`），展平后再校验发布物（`MetadataPublishSchema.xsd`）。
- 独立清单会被再展平一次并替换宏变量；否则发布物里会残留客户端拒绝的 `Include` 与 `${…}`。
- `--webp` 在展平之后执行：`ImagePostProcessor` 调 Imaging 转图，再改写 XML 里的 `Source`。
- 失败一律清空 `Output/` 并返回 1，不留半成品。

## 三、发版用的 Imaging

`Ra3.BattleNet.Metadata.Imaging` 是独立项目，不打进 NuGet 主包，只在 `--webp` 时由 `ImagingInvoker` 以子进程调用。它只负责把图片转成 WebP；主库不引用任何图像库。

## 四、线上部署

| 目标 | 入口 | 说明 |
|---|---|---|
| EdgeOne | `.github/workflows/deploy.yml` | push 到 `main` 后执行 `bash build.sh --webp --content-revision=${{ github.sha }}`，再 `npx edgeone makers deploy ./Output` |
| Cloudflare Pages | `npm run deploy`（`wrangler.toml`） | 本地或 CI 执行 `wrangler pages deploy Output` |

`build.sh` 自己下载 .NET 10 到 `./dotnet`，再调用命令行构建；`--content-revision=` 用来把提交标识写进根节点。

`_redirects`、`edgeone.json`、`editor.html` 如果存在于源目录，会原样复制到输出。

## 五、测试

`Ra3.BattleNet.Metadata.Tests` 使用 MSTest，禁止混入 xunit。覆盖范围：

- 展平与 `Include`、`Base` 继承合并、实体 ID 冲突检测
- 源模式与发布模式的硬失败拦截、ID 与资源断链
- 发布树结构断言、Catalog 与 LINQ 查询 API
- 独立清单解析、`Skudef` 与历史 `Mount` 的挂载计划
- 缓存 JSON 契约、缓存读写节奏、下载边界（`MetadataClient` 三组测试）
- 语言标签、颜色转换、样式继承、启动兼容性、图片后处理

改构建器或缓存行为时，先补测试再改实现；测试失败不允许用重跑掩盖。

## 六、改完必须跑的三件事

```bash
dotnet test Metadata.sln
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
node scripts/verify-docs.mjs
```

前两条证明代码和数据没坏，第三条证明文档没腐烂。
