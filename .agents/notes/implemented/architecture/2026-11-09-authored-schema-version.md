# Agent Note: SchemaVersion 由源根声明

Status: implemented

## Problem

此前 `SchemaVersion` 只由构建注入：`MetadataBuilder.Build` 展平时给每份输出 XML 的根节点写 `SchemaVersion` 和 `ContentRevision`，源树自己不带这个属性。客户端读根清单时用 `CatalogError` 检查 `SchemaVersion`，缺了直接判 `Unavailable`。

结果是“指定入口就能读”这条规则只对发布物成立：作者写好的源树 `Metadata/metadata.xml` 必须先跑一遍构建才能被客户端消费，而构建只是把分散文档合成一份、方便网络传输的步骤。作者想直接把自己的源树指给客户端调试，或者想让源树本身就是可读的单一入口，都做不到。同时命令行还要单独传 `--schema-version=` 决定输出写什么版本，契约版本与作者看到的数据是两处。

## Decision

- `SchemaVersion` 改为作者写在源根 `metadata.xml` 的根节点上（`<Metadata SchemaVersion="1.0">`），作为数据契约版本的唯一来源。
- `MetadataBuilder.Build` 不再注入 `SchemaVersion`；导出复制根节点属性，源上写的版本原样进发布物。构建期标记（`ContentRevision`）随后被 [移除构建期修订标记](2026-11-09-drop-content-revision.md) 一并删除。
- 删除 `Build` 的 `schemaVersion` 参数和命令行 `--schema-version=`。`MetadataBuilder.DefaultSchemaVersion` 保留，`MetadataSchema.Current` 继续引用它，作为库的期望值。
- 构建在读入口后立即检查 `SchemaVersion` 属性，缺失时报「源根 metadata.xml 的根节点缺少 SchemaVersion 属性」，构建失败并清空 `Output/`。
- `MetadataSchema.xsd` 的根类型显式声明可选 `SchemaVersion` 属性，保留 `anyAttribute`，这样被 `Include` 的片段不必带版本。
- 源树与展平产物共用 `MetadataReader` / `MetadataClient` 同一条读取路径；构建不再是读取前置条件。

## Alternatives considered

- **继续由构建注入，只给客户端加“源树宽松模式”**：客户端不用改，源树也能读；但等于让客户端接受没有契约声明的数据，版本兼容判断失去依据，还会把“发布物严格、源树宽松”两套语义带回来。
- **把 `SchemaVersion` 放进 `Tags` 子元素**：能复用现成变量结构；但发布物 schema 已要求它是根属性，同语义两处放法，客户端还得两种都认。
- **命令行显式覆盖，源文件可选**：保留 `--schema-version=` 可以临时发别的版本；但契约版本属于作者声明，构建期覆盖会让同一份源产出不同契约，且与“源树可直接读”互相矛盾。
- **要求每份被 Include 的片段都写 `SchemaVersion`**：校验更一致；但源树拆分本就是为了自由书写，片段带不带版本对读取没有意义，没有必要加这条约束。

## Consequences

- 作者侧新增一条硬约束：入口 `metadata.xml` 根节点必须写 `SchemaVersion`，否则构建失败；被 Include 的片段不需要写。
- 直接把 `RA3BN_METADATA_URL` 指向源树 `Metadata/metadata.xml` 即可被桌面端读取（Debug），不必先构建；源树里的外部清单按需从同一源根懒加载。
- 发布物根节点的 `SchemaVersion` 来自源文件，改写源属性会直接改掉对外契约。
- 旧的构建命令带 `--schema-version=` 会报未知参数，需要删掉该参数；`MetadataBuilder.Build` 的调用方按新签名 `Build(src, dst, convertImages)` 调整。

## Verification

- `dotnet test Metadata.sln` 全绿。
- 新增回归测试：源根缺少 `SchemaVersion` 时 `Build` 抛出含「缺少 SchemaVersion」的 `InvalidOperationException`；等价性测试改为断言源声明被原样保留到发布物。
- 真实 CLI 构建到 `Output/`，产物根节点 `SchemaVersion=1.0`，源 XML、XSD 不进输出。
- 临时探针用 `MetadataClient.RefreshRootAsync` 直接读源树 `Metadata/metadata.xml` 返回 `Fresh`，与读 `Output/metadata.xml` 结果一致。
