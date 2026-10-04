# Agent Note: 保持元数据版本 1.0

Status: implemented

## Problem

Style 字段调整时，默认 SchemaVersion 被提高到 2.0。Desktop 当前按 1.0 检查整个元数据，这会影响模组列表、应用包下载请求和更新信息，不只是页面样式。

## Decision

按用户要求，默认 SchemaVersion 恢复为 1.0，保留已经实现的全部 Style 字段、颜色 Format、按钮状态、继承和校验逻辑，不修改 Desktop。

- `MetadataBuilder.DefaultSchemaVersion` 为 `1.0`；`MetadataSchema.Current` 引用该常量。
- `IsCompatible("1.0")` 返回 true，`IsCompatible("2.0")` 返回 false。显式传入构建版本的现有功能不变。
- Style 测试数据、README 和仓库指南使用当前版本 1.0。
- 本决定只取代 [Mod 页面自定义样式](2026-10-04-mod-style.md) 中提高契约版本的部分；原属性定义和实施验证记录保留。

## Alternatives considered

- 保留 2.0：可以通过版本检查区分新旧样式，但需要 Desktop 同步更新，否则整份元数据被拒绝。用户决定只调整 Style，不提高整体版本。
- 保持 1.0：避免旧客户端因为版本号拒绝读取基础数据；代价是版本检查无法区分新旧样式字段。本次采用此方案。

## Consequences

- 旧客户端的 1.0 版本检查不再因为此次 Style 调整失败。
- 通过版本检查不表示支持新的 Style。页面仍需读取新字段才能显示定制样式；旧样式字段名没有恢复，旧 ARGB 颜色仍需 Format 标记。
- 不修改 NuGet 软件版本、模组版本、Desktop 子模块或线上缓存。

## Verification

- `dotnet test Metadata.sln --nologo`：155 个测试通过，0 个失败，包含接受 1.0、拒绝 2.0 的版本检查。
- 实际运行 CLI 构建到独立临时目录并加载产物：输出 `SchemaVersion=1.0`，成功列出应用 RA3BattleNet、content 和模组 Corona、ArmorRush。
- 临时构建输出使用完后移入回收站，原有 Output 和 Desktop 不变。
