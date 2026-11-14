# Agent Note: 移除构建期修订标记 ContentRevision

Status: implemented

## Problem

`ContentRevision` 是构建时写进发布物根节点的提交标识，文档里一直写成“客户端据此判断内容是否更新”。核对代码后没有这个消费者：

- `MetadataBuilder` 写它，`ValidateHard` 只检查它非空，CLI 构建完打印一次。
- 缓存与更新判断走的是 ETag、Last-Modified、整图 SHA-256（`GraphDigest`）和 composition 清单；`UpdateService` 比的是 `Application` / `Updater` 的 `Version`。
- 桌面端 `AppMetadataReader` 把它塞进 `AppMetadataSnapshot.ContentRevision`，这个字段全仓没有读取点；`MainWindow` 只用版本、公告和状态。
- `ModLibraryStore` 写本地 `mod.xml` 时反而把它删掉。
- 部署流水线把 `github.sha` 传进构建，产物里那一行没有任何下游校验。

结果是发布契约里多了一个必填属性、命令行多一个参数、CI 多一段传递，读的人还以为它能判断更新。

## Decision

- 删除 `ContentRevision`：`MetadataBuilder.Build` 不再接收 `contentRevision`，也不再写这个属性；签名变成 `Build(src, dst, convertImages)`。
- 删除 `ValidateHard` 对它的必填检查、`Program` 的 `--content-revision=` 参数与打印、`build.sh` 的参数转发、`deploy.yml` 里的 `--content-revision=${{ github.sha }}`。
- 发布物 `MetadataPublishSchema.xsd` 不再要求该属性；源树 XSD 注释同步说明构建不追加任何版本或修订属性。
- 桌面端删掉 `AppMetadataSnapshot.ContentRevision` 字段与读取；`ModLibraryStore.StripRuntimeNodes` 只清理 `SchemaVersion`。
- `Tags/Commit` 的 `${TIMESTAMP}` 保留：那是作者写在源里的时间戳字段，构建负责解析，属于数据而不是标记。
- 编辑器判断“这是发布物”的探针从 `ContentRevision` 改成 `Tags/Commit` 是否已被替换成时间戳。

## Alternatives considered

- **保留为可选属性，文档改成“仅供人工排查”**：改动最小，也能定位发布物来自哪次提交；但没有人读、没人校验，留着仍然会被误认为契约的一部分，而且部署那一段传递链也没有意义。
- **改成 HTTP 响应头或构建日志输出**：能保住提交追溯，又不污染数据契约；但需要服务端配合，现阶段排查直接看 CI 记录就够，没必要新增通道。
- **连 `Tags/Commit` 一起删**：时间戳同样没有代码读取；但它是作者可写的数据字段、构建只在解析 `${TIMESTAMP}`，删它超出这次“删标记”的范围，留下。

## Consequences

- 发布物根节点只剩 `SchemaVersion` 一个版本属性；源树与发布物在这点完全同构。
- 旧的构建命令带 `--content-revision=` 会报未知参数；`MetadataBuilder.Build` 调用方按 `Build(src, dst, convertImages)` 调整。
- 桌面端不再有 `ContentRevision` 字段；`AppMetadataSnapshot` 构造参数少一项。
- 需要追溯发版时看 CI 的运行记录，而不是拉 `metadata.xml`。

## Verification

- `dotnet test Metadata.sln` 全绿；等价性测试断言发布物根节点没有 `ContentRevision` 且 `SchemaVersion` 原样保留。
- 真实 CLI 构建到 `Output/`：根节点为 `<Metadata SchemaVersion="1.0">`，无修订属性。
- 桌面端测试全绿，子模块固定到该提交后仍能直接读取源树 `Metadata/metadata.xml`（`Fresh`）。
