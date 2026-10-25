# AGENTS.md

本仓库是红警3战网的元数据中心：`Metadata/` 放原始数据，`Ra3.BattleNet.Metadata` 是解析库兼构建器。下面是硬约束和地图；细节全在 `.agents/`，按需再读，别一次吞完。

## 红线

1. **删除**：禁止 `rm`、`del`、`Remove-Item` 直接删除；一律移到回收站。改软硬链接前先确认。
2. **范围**：用户没点名的文件不动；不顺手重排格式、不顺手重构无关代码。
3. **ID 前缀**：作者只写短名，`{路径前缀}:` 由构建器补。手写冒号前缀直接报错。
4. **失败处理**：校验失败就抛错并清空 `Output/`。不吞异常，不加“先放过”的兜底分支。
5. **测试框架**：只用 MSTest，禁止引入 xunit。
6. **流水线开关**：不要新增 `RUN_STAGE_B` / `StageB` 之类的阶段参数，构建只有一条路。
7. **敏感信息**：不读、不打印密钥、Token、密码；需要时只引用环境变量名。

## 命令

```bash
dotnet test Metadata.sln
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
npm run build:release                  # 构建 + WebP，与线上一致
node scripts/verify-docs.mjs           # 文档与笔记检查
node scripts/verify-docs.mjs --write   # 文档内容确实改了时，重写生成物
```

## 文档地图

| 要做什么 | 读哪份 |
|---|---|
| 改 Mod / 应用 / 公告数据 | [.agents/docs/references/data-authoring.md](.agents/docs/references/data-authoring.md) |
| 写版本清单、`Skudef`、下载来源 | [.agents/docs/references/manifest-and-skudef.md](.agents/docs/references/manifest-and-skudef.md) |
| 改模型字段、样式、语言、启动兼容性 | [.agents/docs/references/metadata-model.md](.agents/docs/references/metadata-model.md) |
| 改解析库、查询 API、磁盘缓存 | [.agents/docs/references/library-api.md](.agents/docs/references/library-api.md) |
| 改构建流水线、Imaging、部署 | [.agents/docs/references/build-and-release.md](.agents/docs/references/build-and-release.md) |
| 了解分层、数据流、不变量 | [.agents/ARCHITECTURE.md](.agents/ARCHITECTURE.md) |
| 写决策笔记、改文档结构 | [.agents/CONTRIBUTING.md](.agents/CONTRIBUTING.md) |
| 查文件路径与行数 | [.agents/docs/generated/paths.md](.agents/docs/generated/paths.md) |
| 查历史决定 | [.agents/docs/generated/notes-index.md](.agents/docs/generated/notes-index.md) |
| 查术语 | [.agents/docs/glossary.md](.agents/docs/glossary.md) |
| 查已知缺口 | [.agents/docs/exec-plans/tech-debt-tracker.md](.agents/docs/exec-plans/tech-debt-tracker.md) |

导航总表在 [.agents/docs/index.md](.agents/docs/index.md)。

## 不变量

- 发布树只有静态文件，根 `metadata.xml` 里不能有 `Include`。
- 完整 ID 由构建器生成；`Manifest`、`Image`、`Markdown` 的登记与引用只用短名。
- 资源地址只来自登记节点的 `Source`，客户端不猜路径、不猜扩展名。
- `SchemaVersion` 保持 `1.0`；改样式、加可选字段不提高版本。
- `Package` 只带 `Version`、`ReleaseDate`、`Manifest`；更新说明写在实体的 `Posts`。
- `Mod` 与 `Application` 必须各有 `zh`、`en` 两条 `DisplayName`；显示名、`Posts`、`Links` 都不从 `Base` 继承。
- 写了 `Skudef` 就不许在 `File` 上写 `Mount`、`Language`、`Package`。
- 缓存布局固定，路径映射是纯函数；网络请求只能走 `IMetadataTransfer`。

## 工作流程

1. **定位**：先读对应专题文档与相关笔记，再看代码。别从零发明已有实现。
2. **改动**：只改用户要求的部分。契约变更先看 `SchemaVersion` 是否受影响。
3. **验证**：改代码跑 `dotnet test Metadata.sln`；改数据或构建器还要跑一次构建并检查 `Output/`；改文档跑 `node scripts/verify-docs.mjs`。
4. **记录**：改行为、改契约、改流程时写一篇 `.agents/notes/implemented/<分类>/yyyy-mm-dd-主题.md`，格式见 CONTRIBUTING。
5. **收尾**：临时脚本和测试产物用完就清走，不要留在仓库里。

## 提交

使用 Conventional Commits：`<emoji> <type>(<scope>): 中文标题`，只暂存本次涉及的路径。
