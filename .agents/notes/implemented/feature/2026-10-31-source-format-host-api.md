# Agent Note: 源格式的导出与清单解析收进库

Status: implemented

## Problem

`LoadSource` 让宿主能读作者源文件之后，Desktop 仍自己实现了三类源格式逻辑：把在线展平树反向导出成单 Mod 作者声明（`ModSourceDeclaration`，315 行）、按 stub 找叶子清单并与 `LocalId` 对齐（`ResolveManifestNode`）、把独立 `Manifest` 节点包一层假 `Metadata` 根再解析。

这些规则的权威定义都在本库：展平、ID 前缀、`Source` 改写、stub 与叶子的局部名对齐。宿主各写一份，Schema 增字段或前缀规则变化时必然漏改一端，出现"库生成、宿主读不出"的分裂。

## Decision

新增三个宿主可直接消费的源格式入口，全部纯转换，不读写磁盘、不发网络请求。

- `MetadataSourceExporter.ExportWhole(root, modId, publishedMetadataUrl)` / `ExportVersion(root, modId, version, publishedMetadataUrl)`：把展平树里的单个 Mod 导出成自包含作者声明。只抄该 Mod 自己的节点和它直接引用的 `Image` / `Markdown` 登记；资源引用压成短 ID（稳定、同类内唯一）；每个 `Package` 指向本目录短清单 ID；`ExportVersion` 只留所选版本并把它设成 `CurrentVersion`。`publishedMetadataUrl` 为空时保留相对 `Source`（本地导入），非空时解析成绝对地址（在线安装按地址取回资源）；带 `Url` 的图片只保留外链，不猜 `Source`。
- `MetadataSourceManifest.Resolve(root, declarationPath, package)`：取版本包声明的清单节点。登记节点已内联清单时直接返回，只有 stub 时按 `Source` 在声明目录内读叶子，并按 `LocalId` 对齐后取节点。
- `MetadataParser.ParseManifest(XElement)` / `ParseManifest(string)`：直接解析独立 `Manifest`，字符串入口兼容"只有清单"与"包含清单的文档"两种写法。
- `MetadataXml.Write` / `ToElement`：把解析出的节点还原成 XML，供抄写清单原文或再解析。

## Alternatives considered

- **继续留在 Desktop**：不用动库，但反向展平与正向展平各维护一套字段知识；Schema 一改就是两端漂移，且 stub/`LocalId` 对齐这类坑要在宿主重复写。
- **只导出原始节点，让宿主自己压短 ID 与改 Source**：库更小，等于把本次要消灭的胶水代码换个位置。
- **把安装事实、目录扫描也搬进库**：这些是客户端运行状态（任务队列、启动互斥、WebView 地址），不属于元数据规范，留在 Desktop。

## Consequences

宿主只保留业务判断（什么时候同步、写哪个目录），不再复刻展平规则。库的公开面增加四个入口，方向都是"源格式 ↔ 内存模型"的转换，与 `LoadSource` 成对。`Export*` 输出的是语义等价的源格式，不恢复发布时丢失的注释与原始拆分。

## Verification

`SourceFormatApiTests` 覆盖：整份导出的版本/短 ID/绝对与相对 `Source`/只抄所属 Mod、`Url` 型图片不猜 Source；单版本导出改 `CurrentVersion`；未知 Mod 与版本报错；导出不改动已加载树；stub→叶子按 `LocalId` 对齐；内联清单不读叶子；缺 `Source` 与叶子 ID 不一致报错；`ParseManifest` 两种入口与非法根节点。`dotnet test Metadata.sln` 与 `build` 为最终校验命令。
