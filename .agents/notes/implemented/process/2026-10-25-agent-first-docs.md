# Agent Note: 文档拆分为人类入口与 Agent 地图

Status: implemented

## Problem

`README.md` 303 行、`AGENTS.md` 226 行，两个文件都在同时承担四类内容：作者写数据的教程、解析库 API、缓存行为、构建与发布流程。同一个事实在两处各写一遍，改一处忘一处；新增的 Mod 作者只能从头读到尾。构建流程改动时，没有机制能发现文档已经过期。

## Decision

按“入口只当地图，细节各归一处”拆开：

- `README.md` 只面向人类：项目定位、三条命令、加一个 Mod 的 5 步、常用任务表。
- `AGENTS.md` 只面向 Agent：红线、命令、文档地图、不变量，正文控制在 130 行以内。
- `.agents/ARCHITECTURE.md` 说明代码分层、构建七个阶段、缓存读取路径与必须守住的不变量。
- `.agents/docs/index.md` 是唯一导航入口，按“要做什么”指向 `.agents/docs/references/` 下五份专题事实源。
- `.agents/docs/generated/` 放两份自动生成的索引：笔记索引与路径地图。
- `scripts/verify-docs.mjs` 负责守卫：笔记目录结构与骨架、生成物是否过期、相对链接是否存在、`AGENTS.md` 是否超行、文档是否孤立。

生成物一律由脚本重写，禁止手改；新增文档必须从入口或索引链到，否则检查失败。

## Alternatives considered

- **只精简、不拆分**：改动最小，但每份文档仍然要同时服务作者与 Agent，事实源依旧重复，过期问题不解决。
- **沿用单一大 `AGENTS.md`**：注入上下文简单，但内容一多就会被任务本身挤掉，且无法机械检查覆盖率与新鲜度。
- **引入 tsx 与多个独立校验脚本**：与 Desktop 仓库一致，但本仓库没有 TypeScript 运行依赖，为了几个正则表达式加一层工具链不划算。改为零依赖 `.mjs`，`node` 直接可跑。
- **把生成物做成 CI 运行时才生成、不入库**：仓库里就没有可读的索引；Agent 读不到的东西等于不存在，所以生成物入库并由 CI 校验一致性。

## Consequences

- 旧链接失效：README 与 AGENTS 内部的锚点已不存在，指向这两个文件的说明改为指向专题文档。模板目录的 README 已改成指向写数据指南。
- 属性表仍写在 Markdown 里，与 XSD 双写，已登记为技术债。
- 上线部署流程不变；新增 `verify.yml` 在 PR 上跑文档检查、单元测试和数据构建。
- 以后改行为、改契约仍按 `CONTRIBUTING.md` 写笔记，笔记索引由脚本重排。

## Verification

`node scripts/verify-docs.mjs` 通过：9 篇笔记、2 份生成物、25 份文档的链接与结构全部合规。`dotnet test Metadata.sln` 293 项通过；`dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output` 输出 `✓ 构建完成`。
