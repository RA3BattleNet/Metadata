# Agent Note: Metadata 解析库的运行期本地缓存

Status: rejected — replaced by the implemented XML-only runtime cache in [2026-10-04-metadata-xml-runtime.md](../../implemented/architecture/2026-10-04-metadata-xml-runtime.md)

## Problem

本提案原本想为根清单、包清单、媒体资源和跨进程快照建立统一缓存，并计划接入一个包含租约和回收机制的 `MetadataRuntime`。这比本次实际需求更宽：用户只要求 Desktop 与 Metadata 统一加载和解析 XML，图片、Markdown、大安装包以及 Updater 增量清单都不属于预加载范围。

## Proposal

本篇提出的多版本对象库、租约、媒体缓存、跨进程指针和 `MetadataRuntime` 接入方案不再采用。实际实现由 [2026-10-04-metadata-xml-runtime.md](../../implemented/architecture/2026-10-04-metadata-xml-runtime.md) 记录：`MetadataClient` 只管理根/叶子 XML、最后有效缓存、ETag、进程内去重和按快照投影；Desktop 负责生命周期和预加载调度。

## Alternatives considered

- **保留本提案的完整缓存框架**：能覆盖媒体缓存、租约回收和多进程共享，但超出 XML-only 需求，增加状态和清理故障面，因此改用按来源 URI 隔离的最后有效 XML 缓存。
- **仅实现根目录缓存**：请求更少，但用户明确需要根目录及所有登记版本包清单，无法满足历史版本离线浏览和启动预加载要求。
- **把 Updater 清单也交给 Metadata**：可以统一 XML 模型，但 Updater 需要保持独立 CLI 和纯本地 Applier，本次只在 Desktop 与 Metadata 间传递更新端点。

## Acceptance criteria

本篇不再作为实施验收标准。实施后的测试和真实冒烟结果记录在 [2026-10-04-metadata-xml-runtime.md](../../implemented/architecture/2026-10-04-metadata-xml-runtime.md) 与 Desktop 的实现笔记中。

## Risks

本提案保留的唯一风险记录是：如果未来需要跨进程共享缓存、媒体离线缓存、磁盘配额或租约回收，应另写方案，不得把本篇的未采用设计当作现行实现。