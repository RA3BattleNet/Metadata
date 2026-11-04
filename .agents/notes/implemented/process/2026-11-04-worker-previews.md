# Agent Note: 分支预览迁移到 Worker Previews

Status: implemented

## Problem

此前部署决定使用 `wrangler versions upload` 创建版本预览 URL，用户现在选择 Cloudflare 新的 Worker Previews 分支预览机制。后者要求配置 `previews`，并对旧 Builds 项目进行一次性后台切换。

## Decision

在 main 的 `wrangler.toml` 添加空 `[previews]`。本项目只有静态资源，没有运行时变量、密钥和存储绑定，因此不添加预览专属资源。`assets` 和兼容日期保持在顶层。Worker 名称保持 `metadata`，生产仍执行 `wrangler deploy`。

预览分支命令使用 `npx wrangler preview`，默认以 Git 分支命名。后台先执行 `bash build.sh --webp`，然后部署该分支的产物。本地 `npm run preview` 仍是构建并启动本地服务，不改成上传操作。锁定的 Wrangler 4.151.1 已支持该功能，无需升级依赖。

这部分取代 [首次 Workers 部署决定](2026-11-04-cloudflare-workers-deployment.md) 中的版本预览机制，保留该笔记的生产与本地服务决定。活跃笔记检索仅命中该篇，与本篇互链。

## Alternatives considered

- **继续 versions upload**：可以使用同一 Worker 的生产配置创建版本 URL，但不是用户指定的新分支 Preview 机制，因此不采用。
- **在 previews 内重复 assets 或添加环境变量**：看起来能明确区分环境，但官方规定 assets 留在顶层；当前没有消费环境变量的代码，因此不增加无用配置。

## Consequences

Cloudflare 旧 Builds 项目需要通过 Set up Worker Previews 确认不可逆的 Switch to Worker Previews，并开启 Enable Preview Builds。已有功能分支必须合并或 rebase 包含此配置的 main 后再构建，新分支自然继承该块。

成功部署返回跟随分支最新部署的 Preview URL 和单次部署的 Unique Deployment URL，不替换生产流量。仓库配置不会自动执行后台切换，也不会保证修复此前 CI Worker tag 不匹配；后者若再次出现仍需检查后台绑定，不能跳过身份校验。

## Verification

- 安装锁文件中的 Wrangler 4.151.1，使用其配置解析器确认空 previews 块与顶层 assets 被接受。
- `npm run build:release` 成功，完成 5 张图片转换。
- Wrangler 4.151.1 `deploy --dry-run` 成功。
- `npm run dev` 启动成功，HTTP `/` 返回 301 到 `/metadata.xml`，后者返回 200 且根元素为 Metadata。
- 未调用远程 `wrangler preview`：该命令没有 dry-run 选项，真实分支 Preview 与后台一次性切换仍需云端验证。

参考：[构建分支](https://developers.cloudflare.com/workers/ci-cd/builds/build-branches/)、[入门](https://developers.cloudflare.com/workers/previews/get-started/)、[配置](https://developers.cloudflare.com/workers/previews/configuration/)。
