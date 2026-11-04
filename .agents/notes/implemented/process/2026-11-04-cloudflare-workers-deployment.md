# Agent Note: Cloudflare 部署统一使用 Workers 静态资源

Status: implemented

## Problem

Cloudflare 后台项目是 Workers，但 npm 发布和本地服务仍调用 Pages 命令。Wrangler 的自定义构建还会在已有构建后重复执行 Linux SDK 安装脚本，导致 Windows 本地预览失败。

## Decision

npm 发布使用 `wrangler deploy`，本地服务使用 `wrangler dev`。`wrangler.toml` 只保留 Worker 名称、兼容日期与指向 `Output` 的静态资源配置，不再声明自定义构建。

Cloudflare 后台构建运行 `bash build.sh --webp`，生产发布运行 `npx wrangler deploy`，预览分支运行 `npx wrangler versions upload`。生产与预览都使用 Node 22，并启用需要预览的分支和 Preview URLs。后台构建步骤不调用包含生产发布操作的 `npm run deploy`。

删除未被配置引用的 JavaScript 透传入口。保留 `_redirects`，由 Workers 静态资源处理根路径跳转。EdgeOne 发布流程保持不变。

## Alternatives considered

- **继续使用 Pages**：适合静态站点并提供分支预览，但与用户现有 Workers 项目不一致，需要迁移后台项目，因此不采用。
- **保留 Wrangler 自定义构建**：直接运行 Wrangler 时可以自动构建，但会与后台构建步骤重复，并使本地服务执行 Linux SDK 安装脚本，因此由调用方明确负责构建。

## Consequences

本地需要安装 .NET 10 SDK；`npm run preview` 构建后启动服务，`npm run dev` 服务已有产物。静态服务不需要自定义 Worker 代码。后台配置仍需人工调整，仓库文件不会自动修改 Cloudflare 项目设置。预览版本上传不切换生产流量。

## Verification

- `npm run dev -- --ip 127.0.0.1 --port 8799` 成功启动 Workers 本地服务，未触发 SDK 安装。
- HTTP 请求 `/` 返回 301，Location 为 `/metadata.xml`；请求 `/metadata.xml` 返回 200 和 `application/xml`，根元素为 `Metadata`，无 `Include`。
- `wrangler deploy --dry-run` 成功，未向 Cloudflare 发布。
- 验证使用本地已安装的 Wrangler 4.68.0；锁文件中的 4.151.1 未在本次重新安装，云端实际构建尚未验证。
