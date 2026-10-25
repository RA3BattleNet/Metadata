# 知识库索引

改哪块，先读哪份。文档只有一份事实源，别在对话里重复推演。

| 我要做的事 | 先读 | 关键约束 |
|---|---|---|
| 新增或修改 Mod、应用、公告 | [references/data-authoring.md](references/data-authoring.md) | 目录即命名空间、ID 全仓库唯一、短名由构建器补前缀 |
| 写版本清单、`Skudef`、下载来源 | [references/manifest-and-skudef.md](references/manifest-and-skudef.md) | 文件表不进大总表；写了 `Skudef` 就不许在 `File` 上写挂载属性 |
| 改 XML 模型、样式、语言、启动兼容性 | [references/metadata-model.md](references/metadata-model.md) | `SchemaVersion` 保持 `1.0`；显示名 `zh`/`en` 必填 |
| 改解析库、客户端查询、磁盘缓存 | [references/library-api.md](references/library-api.md) | 发布树布局是纯函数；下载边界只在 `IMetadataTransfer` |
| 改构建流水线、CI、发版 | [references/build-and-release.md](references/build-and-release.md) | 失败即清空输出；Imaging 只在发版跑 |
| 了解代码分层与数据流 | [../ARCHITECTURE.md](../ARCHITECTURE.md) | 依赖只能单向 |
| 查某个文件在哪、多少行 | [generated/paths.md](generated/paths.md) | 自动生成，以表为准 |
| 追溯以前为什么这么定 | [generated/notes-index.md](generated/notes-index.md) | 自动索引；原文在 `.agents/notes/` |
| 写决策笔记、改文档 | [../CONTRIBUTING.md](../CONTRIBUTING.md) | 非平凡改动必写；文档必须挂到本索引 |
| 找术语的准确说法 | [glossary.md](glossary.md) | 一个概念一个词 |
| 看工程底线 | [core-beliefs.md](core-beliefs.md) | 能用脚本查的规则不靠口头 |
| 看已登记的质量缺口 | [QUALITY_SCORE.md](QUALITY_SCORE.md) / [exec-plans/tech-debt-tracker.md](exec-plans/tech-debt-tracker.md) | 缺口要有负责人和下一步 |

---

## 文档分层

1. **入口**：`README.md` 给人看；`AGENTS.md` 给 Agent 看，只当地图。
2. **专题事实源**：`docs/references/` 下按任务划分，每个主题只有一份。
3. **自动生成**：`docs/generated/` 由 `scripts/verify-docs.mjs` 生成，CI 校验新鲜度。
4. **历史决策**：`.agents/notes/` 按生命周期与分类存放，只追加不改写。
