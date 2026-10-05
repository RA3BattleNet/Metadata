# Agent Note: Mod 与 Application 的必填显示名

Status: implemented

## Problem

Mod 与 Application 是客户端产品列表、应用包下载与增量更新任务里直接展示给用户的实体。此前只有 `Mod` 有可选的 `<DisplayName>`，`Application` 根本没有显示名字段，客户端无法从实体数据里拿到稳定、可本地化的产品名，只能依赖固定的界面文案。

同时要避免这次改动牵动既有契约：`SchemaVersion` 必须保持 `1.0`，`UpdateKind@DisplayName` 的属性契约、解析与读写代码都不能改，历史安装记录也要继续可读。

## Decision

给 Mod 与 Application 都增加实体自有的、必填的多语言显示名，并让 Application 的投影与 Mod 对齐。

### 1. 模式（XSD）

- `MetadataSchema.xsd` 与 `MetadataPublishSchema.xsd` 的 `ApplicationType` 在 `Version` 之后新增可重复的 `<DisplayName type="LocalizedTextType">`（`minOccurs=0 maxOccurs=unbounded`，结构上向后兼容，便于历史数据仍可解析）。
- `ModType` 的 `DisplayName` 元素保持不变；是否“必填”由构建期校验决定，不塞进 XSD。
- 两个 schema 的改动保持同步，发布物与源树一致；`SchemaVersion` 仍为 `1.0`。

### 2. 构建校验（`MetadataBuilder.ValidateHard`）

合并、展平之后对每个 `Mod` 与 `Application` 执行实体显示名校验：

- 必须各写一条 `zh-CN` 与 `en-US`（完整语言标签精确匹配，比较不区分大小写）；
- 完整语言标签不能为空白、同一实体内不能重复（不区分大小写）；
- 文本不能为空白；
- 允许再声明其它语言。

校验失败让核心构建直接报错，不写防御式兜底。显示名不能被 `Base` 补齐——`BaseType` 本就不允许 `DisplayName`，实体自身必须写全。

### 3. 继承（`MetadataInheritance`）

- `ModChildOrder` / `ApplicationChildOrder` 加入 `DisplayName`，保证合并后元素顺序与 XSD 一致（Mod：`CurrentVersion, Icon, DisplayName, Style, …`；Application：`Version, DisplayName, TransferAd, …`）。
- `MergeEntity` 对 `DisplayName` 单独处理：**只照抄子实体的全部 `DisplayName` 节点**，不从 `Base` 继承，也不因 `Element(name)` 只取第一个而丢掉后续语言。这样“实体自有、可多条”的语义在继承下完整保留。

### 4. 查询投影（`MetadataQueryExtensions` / `MetadataQueryModels`）

- `ApplicationEntry` 新增 `IReadOnlyList<LocalizedTextEntry> DisplayNames`，参数顺序与 `ModEntry` 对齐：`(Id, Version, DisplayNames, Packages, Raw)`。
- `ToApplication` 复用现有的 `ReadDisplayNames`，与 Mod 行为一致（节点顺序即声明顺序）。`LocalizedTextEntry(Language, Text)` 不变。

### 5. 明确的“不修改”边界

- `UpdateKindType` 的 `@DisplayName` 属性、`UpdaterEndpoint.DisplayName` 及其读写代码全部保持不变；UI 需要时在展示层覆盖。
- `MetadataBuilder.DefaultSchemaVersion` / `MetadataSchema.Current` 仍为 `1.0`。
- 旧安装记录里的任务名等历史数据不参与本次实体校验，继续按原方式读取。
- 通用解析器（`MetadataParser` / `Metadata`）不新增全局校验，校验仍集中在构建期。

## Alternatives considered

- **把 `DisplayName` 直接写成 XSD 必填（`minOccurs=1`）**：XSD 无法表达“必须各写一条 `zh-CN` 与 `en-US`、且完整标签不区分大小写、不能重复”，只能校验“至少一条”，错误信息也不如构建期清晰；采用构建期校验。
- **让 `Base` 提供默认显示名**：与“显示名属于实体自身”的语义冲突，且会掩盖缺失，用户明确要求不从 Base 继承。
- **复用 `UpdateKind@DisplayName`**：更新线显示名描述的是更新器/渠道，不是产品实体本身，两者契约不同，用户明确要求 `UpdateKind` 保持不变。

## Consequences

- 正式元数据（Corona、ArmorRush、NeuroEva、content、RA3BattleNet）统一使用 `zh-CN` 与 `en-US` 两条实体显示名。
- 模板、README、`AGENTS.md` 与 Templates 指南同步说明必填规则与“不从 Base 继承”。
- 任何遗漏显示名的实体都会在构建期被拦截；历史发布物仍按 `1.0` 读取。
- 客户端可以在产品列表、包下载与增量更新任务中统一使用实体显示名，固定应用外壳/通知品牌文案仍由客户端本地 i18n 负责。

## Verification

本分支仅完成代码、数据与文档改动，未运行构建/测试/格式化；由父任务在工作树汇合后统一执行 `dotnet test Metadata.sln` 与 CLI 构建验证，并核对：
- 缺失或重复显示名的实体被构建期拦截，且只有完整的 `zh-CN` / `en-US`（大小写不敏感）被接受；
- 子实体继承 `Base` 时，实体自身的 `DisplayName` 全部保留且不从 Base 继承；
- 发布产物 `SchemaVersion` 仍为 `1.0`，`UpdateKind@DisplayName` 行为不变。
