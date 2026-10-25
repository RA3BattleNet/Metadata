# 红警3战网元数据仓库

红警3战网客户端的元数据中心。这里做两件事：

1. **放数据**：Mod 作者提交 XML、图片、更新说明和版本清单，CI 构建成静态文件，客户端联网下载。
2. **读数据**：`Ra3.BattleNet.Metadata` 解析库，客户端用 URL 或本地目录都能读同一套数据。

改数据看[写数据指南](.agents/docs/references/data-authoring.md)；改代码看[知识库索引](.agents/docs/index.md)。

---

## 快速开始

需要 .NET 10 SDK。本地构建不跑图片转换，线上发版才转 WebP。

```bash
dotnet test Metadata.sln                                              # 单元测试
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
```

构建成功会打印 `✓ 构建完成` 和实体列表，产物在 `Output/`。任何校验不通过都会清空输出并说明改哪个文件。

## 加一个 Mod

1. 复制 `Metadata/Templates/Mod.xml` 到 `Metadata/mods/<英文名>/<英文名>.xml`。
2. 把 `<Mod ID="…">` 改成全仓库唯一的名字。
3. 图片和 Markdown 放进同一目录，`Source` 填相对路径。
4. 在 `Metadata/mods/mods.xml` 里加一行 `<Include Source="<英文名>/<英文名>.xml" />`。
5. 跑一次上面的构建命令。

别忘了 `zh` 和 `en` 两条 `DisplayName`。要写版本清单、`Skudef`、样式或启动兼容性，看[写数据指南](.agents/docs/references/data-authoring.md)。

## 常用任务

| 任务 | 命令 |
|---|---|
| 本地构建产物 | `npm run build` |
| 构建并转 WebP（与线上一致） | `npm run build:release` |
| 本地起静态服务看产物 | `npm run preview` |
| 发布到 Cloudflare Pages | `npm run deploy` |
| 检查文档与笔记 | `node scripts/verify-docs.mjs` |

发版到 EdgeOne 由 `.github/workflows/deploy.yml` 在推送 `main` 时自动执行。细节见[构建与发布](.agents/docs/references/build-and-release.md)。

## 仓库结构

```text
Metadata/                         源数据；metadata.xml 是总入口
Ra3.BattleNet.Metadata/           解析库 + 命令行构建工具
Ra3.BattleNet.Metadata.Imaging/   发版用的 WebP 转换工具
Ra3.BattleNet.Metadata.Tests/     MSTest 测试
scripts/verify-docs.mjs           知识库检查
.agents/                          研发文档、决策笔记、自动生成的索引
```

## 文档在哪看

- [知识库索引](.agents/docs/index.md)：按任务查该读哪份。
- [系统架构](.agents/ARCHITECTURE.md)：数据怎么从 XML 变成客户端能读的文件。
- [决策笔记索引](.agents/docs/generated/notes-index.md)：以前的改动为什么这么定。
- [术语表](.agents/docs/glossary.md)：文档里每个词的确切含义。
- [贡献规范](.agents/CONTRIBUTING.md)：改行为怎么留记录、文档怎么放。

## 许可证

见 [LICENSE](LICENSE)。
