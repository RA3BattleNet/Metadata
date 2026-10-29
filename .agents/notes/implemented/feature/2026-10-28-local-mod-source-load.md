# Agent Note: 本地作者源文件内存读取

Status: implemented

## Problem

Desktop 需要直接读作者磁盘上的 Mod 源文件（`corona.xml`、独立清单、管理目录里的 `mod.xml`），但库只提供两条路：`MetadataBuilder.Build` 要求整棵源树、XSD 与输出目录，`MetadataParser.Load` 又只接受已展平产物。宿主只能先跑一次构建或自己拼 XML，既写盘也绕开既有展平规则。

## Decision

新增 `MetadataBuilder.LoadSource(entryPath, sourceRoot = null)`：在内存里复用 `MetadataFlattener` 展开 Include、合并 `Base`/`InheritFrom`、限定短 ID，直接返回已展平的 `Metadata` 树。

- 源根缺省为入口文件所在目录；ID 前缀是文件相对源根、去 `.xml` 的路径，与构建一致。
- 资源 `Source` 改写为相对源根的路径：先按定义文件目录、再按源根解释；被 Include 进来、已经限定的登记节点不再二次解释，避免父文件目录误读 root 相对路径。
- 入口、Include 与资源路径都收敛在源根内，越界在 `File.Exists` / 读取前抛 `InvalidOperationException`。
- 不写文件、不替换 `${TIMESTAMP}` / `${ENV:...}`、不做 XSD 校验，也跳过跨实体 `Compatibility` 校验（单模组没有整棵目录）；`Build` 仍然全量校验。
- 新增 `MetadataParser.Load(XDocument)`，内存文档无需临时文件即可解析，仍然拒绝 `Include`/`Includes`/`Module` 与非法根节点。
- `MetadataBuilder.Load` / `LoadFromFile` 的严格语义不变：发布物入口依旧拒绝源树。

规则写进 [library-api.md](../../../docs/references/library-api.md)，实现在 [MetadataBuilder.cs](../../../../Ra3.BattleNet.Metadata/MetadataBuilder.cs) 与 [MetadataFlattener.cs](../../../../Ra3.BattleNet.Metadata/MetadataFlattener.cs)。

## Alternatives considered

- **要求宿主先构建再读 Output**：零新增 API，但写盘、需要完整源树，还得等变量替换，管理目录里的单 Mod 无法读取。
- **另写一套独立解析器**：可以只处理单文件，但 Include、继承、前缀与资源改写会出现第二套规则，迟早与构建结果不一致。
- **放宽 `MetadataParser.Load` 接受源树**：调用点少，但发布物严格入口会被污染，客户端可能拿到未展平文档。

## Consequences

Desktop 可以只读地读取作者源文件，并在内存里与在线元数据对齐（同一源根下 `Source` 可直接交给 `MetadataResourceUri.Resolve`）。手写 `<Manifest ID Source>` 的登记 ID 带入口文件前缀，Include 生成的 stub 与单独加载叶子则按文件路径前缀，两者需用 `MetadataFlattener.LocalId` 对齐局部名。现有源树的构建产物不变；构建与本地读取遇到越出源根的 Include / 资源会直接失败，不再静默留下不可达路径。源目录不被写入。

## Verification

`SourceLoadTests` 覆盖：Include 与继承展平、嵌套文件资源相对改写、Corona 风格目录的清单 stub、手写 stub 与叶子 ID 的局部名一致、循环 Include、入口 / Include / 资源越界在读取前拒绝、读取前后作者文件逐字节不变、不解析构建期变量、无其他 Mod 时跳过 Compatibility、以及 `Load` 仍拒绝源树。`dotnet test Metadata.sln` 与 `dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output` 为最终校验命令。
