# Agent Note: 统一元数据读取入口

Status: implemented

Related decision: [one composition core and layout-only exports](../../implemented/architecture/2026-11-07-composable-metadata.md) partially overlaps this decision. It replaces Include-only branching while preserving strict Load, source abstraction and historical evidence; this Decision is not rewritten.

## Problem

元数据读取此前存在两条独立路径：`MetadataBuilder.Load` 只接受已展平的发布物，遇到 `Include` 直接报错；`MetadataBuilder.LoadSource` 只支持本地作者源树，而完整构建 `Build` 又要求整棵源树与输出目录。宿主若想在同一个调用点兼顾作者源文件与线上发布物，必须自行预读并判断文件类型；同时 `MetadataFlattener` 内部直接调用 `System.IO`，远端 HTTP 源树无法复用展平逻辑；此外还存在菱形 Include 重复展开、单文件内同名登记 ID 静默覆盖、以及越界路径检查口径不统一等边界问题。

## Decision

新增统一入口 `MetadataBuilder.Open(pathOrUrl, options)` 与配套抽象，将源树展平与发布物直接解析整合在一起：

- **自动识别与选项**：由 `MetadataDetector.HasInclude` 检查入口 XML 是否包含 `Include`/`Includes` 元素，有则走展平，无则走发布物解析。通过 `MetadataSourceOptions` 提供 `SourceRoot`（源根目录）、`ForceSourceTree`（无 Include 也强制展平）、`ValidateReferences`（开启断引用校验）与 `CustomSource`（注入自定义 XML 源）。
- **只读 XML 源抽象**：定义 `IMetadataXmlSource` 接口，把存在性检查、文档流读取与路径运算（`Combine`、`GetRelativePath` 等）从 `MetadataFlattener` 抽离。提供针对本地磁盘的 `PhysicalFileXmlSource` 与针对远端地址的 `HttpXmlSource`。远端路径以绝对 URL 表达，跨主机或越出源根的引用直接拒绝。
- **既有入口契约保持稳定**：`MetadataBuilder.Load` 仍只认发布物；`MetadataBuilder.LoadSource` 保持签名不变，内部委托 `Open` 并开启 `ForceSourceTree = true`；`MetadataParser.Load` 继续严格拒绝 `Include`/`Includes`/`Module`。
- **引用完整性校验**：新增 `MetadataValidator.EnsureReferences` 与 `FindMissingReferences`，以登记节点（带 `ID` 的 `Image`/`Markdown`/`Manifest`）为合法候选集合，检查实体与样式的引用字段，并在发现断引用时抛出明确异常。
- **四个边界缺陷收紧**：
  1. 菱形 Include 去重：会话内记录已展开文件，再次遇到跳过展开，避免重复并入实体和登记节点。
  2. 同文件登记 ID 查重：同文件内声明的登记短名查重（含仅大小写差异），冲突抛出 `InvalidOperationException`。
  3. 路径越界前置拦截：入口、`Include` 与资源 `Source` 经 `..`、绝对路径或 UNC 越出源根时，在读取或探测目标前直接拒绝。
  4. 大小写口径统一：同一文件内登记短名查重与 ID 唯一性判定统一按 `OrdinalIgnoreCase`，仅大小写不同的重复登记也抛 `InvalidOperationException`。

规则同步记录于 [library-api.md](../../../docs/references/library-api.md)，此前本地源树机制见 [2026-10-28-local-mod-source-load.md](./2026-10-28-local-mod-source-load.md)。
当前 [MetadataBuilder.cs](../../../../Ra3.BattleNet.Metadata/Building/MetadataBuilder.cs) 仅负责构建；本篇当时的 Open/MetadataDetector/MetadataFlattener 实现已移除，见 [single-format 决定](../../implemented/simplification/2026-11-07-single-metadata-format.md)。低层 XML 投影与引用校验分别位于 [Reading/MetadataParser.cs](../../../../Ra3.BattleNet.Metadata/Reading/MetadataParser.cs)、[Reading/MetadataValidator.cs](../../../../Ra3.BattleNet.Metadata/Reading/MetadataValidator.cs)，当前单一读取与目录责任见 [集成计划](../../../docs/exec-plans/2026-11-07-composable-metadata.md)。上文历史 Decision 不据此改写。

当前读取实现位于 [Reading/MetadataReader.cs](../../../../Ra3.BattleNet.Metadata/Reading/MetadataReader.cs)，选项与 XML 来源边界分别见 [MetadataSourceOptions.cs](../../../../Ra3.BattleNet.Metadata/Reading/MetadataSourceOptions.cs)、[IMetadataXmlSource.cs](../../../../Ra3.BattleNet.Metadata/Reading/IMetadataXmlSource.cs)、[PhysicalFileXmlSource.cs](../../../../Ra3.BattleNet.Metadata/Reading/PhysicalFileXmlSource.cs) 和 [HttpXmlSource.cs](../../../../Ra3.BattleNet.Metadata/Reading/HttpXmlSource.cs)。这些是现行实现路径，不表示旧模式选项继续存在。

## Alternatives considered

- **放宽 `MetadataBuilder.Load` 直接支持源树**：调用点看似最少，但这会让发布物严格校验失去防线，客户端可能静默载入未展平的源树半成品。
- **将 HTTP 读取与下载完全留给宿主**：外部宿主可通过下载整包或挂载本地目录再调用，但在线调试作者源树是常见场景，标准库引入轻量只读流式源即可支持，无需宿主编写大量胶水代码。
- **源树模式下求值构建期变量**：如果在源树模式下替换 `${TIMESTAMP}` 等变量，每次读取得到的时间戳与指纹都不一致，破坏了只读解析的幂等性与可复现性。

## Consequences

- **等价性保证**：现有源树构建产物与源树直接读取在语义上完全一致。基于真实仓库数据（源树 661 节点 / 82 个登记 ID / 17 个实体），源树展平与完整构建产物逐字对齐，仅有的差异是预期的构建期字段（根 `ContentRevision` 与 `Tags/Commit`）。
- **HTTP 源行为与开销特征**：`HttpXmlSource` 的 XML 正文按规范化 URL 缓存，因此同一 XML 先 `Exists` 再 `LoadDocument` 只会发起一次 HTTP GET 请求；配置 60 秒超时；自建处理器时关闭自动重定向，遇到 3xx 抛错；仍然没有多地址重试和断点续传，那部分由发布物专用的 `Load` / `MetadataClient` 承担。
- **资源生命周期**：`HttpXmlSource` 实现 `IDisposable`，仅释放自身创建的 `HttpClient`，外部注入的 handler 归调用方所有；`MetadataBuilder.Open` 内部创建的源实例在用完后立即以 `using` 释放，宿主传入的 `CustomSource` 则归调用方管理。

## Verification

- `dotnet test Metadata.sln` 372 个测试全绿，涵盖 `OpenMethodTests`、`EquivalenceBaselineTests`、`HttpSourceTests`、`ValidationTests`、`PathSecurityTests` 与 `PerformanceTests`。
- `EquivalenceBaselineTests` 验证源树读取与发布物读取逐字等价，整仓源树读取本机实测约 4 ms，测试断言上限 500 ms。
- `dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=<临时目录>` 冒烟成功，产物 `metadata.xml` 绝无 `Include` 节点。
