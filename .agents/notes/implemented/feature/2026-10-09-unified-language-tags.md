# Agent Note: 语言标签统一为 zh 与 en

Status: implemented

## Problem

仓库里跟语言有关的地方写法不统一：实体显示名、新闻标题／简介／正文、友情链接 `Languages`、清单 `AddBig@Language` 各写各的，`zh-CN` / `en-US` 与 `zh` / `en` 混用。客户端按标签查元数据时，任何一处写法不一致都会静默取不到内容——`zh-CN` 命中不了 `zh`。同时客户端自己也在多处硬编码 `zh-CN` / `en-US`，与元数据里的写法各走一套。

需要一次把标签收敛到一种写法，并让还带着地区写法的查询入口不至于取不到内容。

## Decision

全项目的规范化语言标签是 `zh` 与 `en`。

- 正式元数据（Corona、ArmorRush、NeuroEva、content、RA3BattleNet）、模板、README 与 `AGENTS.md` 一律写 `zh` / `en`。`UpdateKind` 只有 `DisplayName` 属性、不带语言标签，不受影响。
- 构建期仍然精确要求 `zh` 与 `en` 各一条实体显示名：`zh-CN` / `en-US` 不能满足必填，写错就直接构建失败，避免规范标签再次被绕开。
- 解析库新增 [`LanguageTag`](../../../../Ra3.BattleNet.Metadata/LanguageTag.cs)，只做比较、不改写节点原文。比较是单向的语言族降级：查询带地区时归到语言族再比（`zh-CN` / `zh-TW` 都按 `zh` 查，`en-US` 按 `en` 查），不带地区的查询只精确匹配，比较不区分大小写，`en` 不匹配 `english`。反向不做：查询 `zh` 不会命中数据里的 `zh-CN`。`LanguageTag.Select` 取第一条匹配的条目。
- Desktop 侧不再硬编码标签：`LocalizationService.TagOf` 是唯一出处，`Settings.json` 的 `Language`、`ClientHostService.GetLanguageAsync()` 的返回值、元数据查询都只写 / 只查 `zh` / `en`；实体显示名、公告正文与链接标题经 `LanguageTag.Select` 取。读写入口仍按 `zh` / `en` 前缀兼容历史值（系统 UI 语言、旧 `Settings.json`、网页 `SetLanguage` 入参），认出来后一律按规范值处理与写回。
- 游戏语言包（`ModLanguage`、`AddBig@Language`、skudef 文件名里的 `chinese_t` / `english`）是另一套标识，保持不动。

## Alternatives considered

### 让构建器也接受 `zh-CN` / `en-US` 作为必填项

那样规范就又分裂成两套写法，校验也说不清“到底该写哪个”。源头强制收敛到 `zh` / `en`、只在读取端做语言族降级，才能既统一新数据又读得动旧请求。

### 双向匹配（查询 `zh` 也命中数据里的 `zh-CN`）

数据侧统一写成 `zh` / `en` 之后，带地区的写法只可能来自查询侧，双向匹配用不上；多一条规则就多一处要理解的分支。只做单向降级。

### 在解析库加载时把 `zh-CN` 重写成 `zh`

会篡改节点原文，`Raw` 与叶子 XML 对不上，排查线上问题时看到的标签和实际数据不一致。改由比较函数处理，标签原文保持原样。

### 客户端各自实现前缀匹配

重复实现会再次分叉：一处按前缀、一处按精确，回到原点，Desktop 的 `ModHostApi` 里就曾有一份自己的 `LanguageFamily`。统一收到 `LanguageTag` 一处。

## Consequences

- 新数据只有一种规范写法，`zh-CN` / `en-US` 不会再出现在源里，构建期就会拦住。
- 使用新解析库的调用方可以把历史 `zh-CN` / `en-US` 查询降级到新数据的 `zh` / `en`；旧客户端仍使用精确匹配，不能获得这项兼容能力。Metadata 发布新数据后，旧客户端可能缺少显示名和公告，直到客户端更新到 Desktop #48；两边的发布顺序存在这个展示退化窗口。数据侧仍写着区域标签、查询侧已经改成 `zh` / `en` 的组合也读不到内容，这条按不反向匹配的取舍接受。
- 代价：`LanguageTag.Matches` 会把 `zh-TW` 同类地区写法都归到 `zh`，不区分繁简——当前界面只有中英两种语言，需要区分时再按地区标签精确匹配。
- 校验与匹配是两套行为（构建期精确、读取期降级），`README` 与 `AGENTS.md` 已分别写明，避免被当成同一件事。

## Verification

- `Metadata`：`dotnet test Metadata.sln`（`SchemaTests` 覆盖 `zh-CN` / `en-US` 不再满足必填；`LanguageTagTests` 覆盖语言族降级、反向不命中、大小写与 `english` 边界）；`npm run build` 展平并校验全部源 XML。
- `Desktop`：`dotnet test Ra3.BattleNet.Desktop.Tests`（`ModCatalogCacheTests.ResolveLinkTitle_*` 覆盖链接标题的语言族降级，`LocalizedDisplayNameTests` 覆盖显示名按语言族取，`ClientHostServiceTests` 覆盖设置写入规范标签）。
