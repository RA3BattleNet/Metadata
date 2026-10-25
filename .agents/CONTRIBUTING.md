# 贡献与决策笔记规范

两件事：非平凡改动留下决策笔记；文档改动遵守固定结构。两者都有脚本兜底。

---

## 一、什么时候要写决策笔记

**要写**：改了行为、改了架构、改了跨文件契约、改了数据格式或落盘布局、改了构建与发布流程、改了测试策略。

**不要写**：纯排版、改错别字、依赖补丁、版本打标、改一个文件就能看懂的局部修复。这类改动直接改代码，别为了凑数立笔记。

## 二、笔记放在哪、怎么命名

```text
.agents/notes/{生命周期}/{分类}/yyyy-mm-dd-主题.md
```

- 生命周期：`proposed`（提案）、`implemented`（已实施）、`rejected`（已否决）、`archived`（封存，只读）。
- 分类：`feature`、`bug-fix`、`simplification`、`architecture`、`process`、`testing`。
- 一句话主题用小写英文短横线，例如 `2026-10-11-mod-launch-compatibility.md`。
- 禁止手写 `INDEX.md`；索引由脚本生成到 `.agents/docs/generated/notes-index.md`。

## 三、笔记的固定骨架

```markdown
# Agent Note: 标题

Status: implemented

## Problem

现在哪里不对、谁受影响，一两段说完。

## Decision

决定做什么，关键字段、边界、默认值写清楚。

## Alternatives considered

- **被否决的做法**：它能带来什么好处，为什么这次不选。

## Consequences

上线后行为有什么变化，作者和客户端各自要做什么。

## Verification

用什么测试或命令证明它成立。
```

硬规则（脚本会查）：

- 第 1 行必须是 `# Agent Note: 标题`，第 3 行是 `Status: implemented`。`rejected` 要写成 `Status: rejected — 被谁取代或为什么`。
- 第一节必须是 `## Problem`；`implemented` 必须有 `## Decision`、`## Consequences`；`rejected` 必须有 `## Proposal`。
- `## Alternatives considered` 必填，写真实考虑过的方案，先写它的好处再写否决原因。
- 已实施的笔记里不允许保留 `## Proposal`、`## Plan`、`## Acceptance criteria` 这类提案期章节。
- 笔记里指向仓库文件的相对链接必须真实存在。

## 四、笔记怎么改

- **事实变了就地改**：类名、路径、参数、默认值变了，直接更新原笔记对应句子，不要留旧值。
- **决定被推翻就新立一篇**：新笔记开头链到旧笔记，旧笔记的状态保持原样，不要把人家的 `## Decision` 改成反面。
- **归档区只读**：`archived/` 下的笔记由 `.agents/notes/archived/manifest.json` 记录 SHA256，改动会被脚本拦下。

## 五、其他文档怎么改

- `README.md` 只面向人类：项目是什么、怎么跑起来、怎么加一个 Mod。
- `AGENTS.md` 只面向 Agent：红线、命令、文档地图。控制在 130 行正文以内，细节一律放到 `.agents/docs/` 并在索引里挂上。
- `.agents/docs/index.md` 是唯一导航入口。新增文档必须从这里（或已有文档）链得到，否则脚本判为孤立文档并报错。
- `.agents/docs/generated/` 下的文件只由脚本生成，禁止手改。
- 术语统一使用 [glossary.md](docs/glossary.md) 里的写法；不要另造同义词。

## 六、提交前自检

```bash
node scripts/verify-docs.mjs          # 检查笔记结构、生成物新鲜度、链接、AGENTS.md 体积
node scripts/verify-docs.mjs --write  # 内容确实变了时，先重写生成物再检查
dotnet test Metadata.sln
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
```

脚本报错会直接告诉你要改哪个文件；改完重跑，直到输出 `ok:`。
