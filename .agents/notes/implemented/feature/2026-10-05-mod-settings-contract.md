# Agent Note: 通用模组设置与依赖注入契约

Status: implemented

## Problem

根模组无法声明用户可配置项，版本清单也没有把「条件动作」与「稳定 DLL 身份」分开：DLL 依赖只能写物理文件名与哈希，客户端启动准备阶段对所有 `Dependencies/Dll` 一刀切强校验，无法表达「仅某个选项激活时才需要的可选注入」。设置、引用与注入都需要一套通用、可扩展、以稳定 ID 定位的契约，而不是把具体模组名或 DLL 文件名硬编码进枚举。

## Decision

### 数据模型与公共 API

- `Mod/Settings` 定义 `Boolean` 与 `Choice` 两种设置，`Boolean@Default` 只能是 `true`／`false`，`Choice@Default` 必须是某个 `Option@Value`；`Option@Value` 同组唯一。动态文案 `DisplayName`／`Description` 的 `@Language` 使用标准语言族（`zh`／`en`，可扩展其它 ISO 主语言族）。既有根级 `DisplayName`、`links`、`posts` 保留地区标签，不顺改。
- `Manifest/Dependencies/Dll` 增加稳定局部 `@ID`、注入 `@Protocol`（`lyi-create-process`／`easyhook`）与类型化 `<CustomData Encoding="utf8"><RuntimeValue Name="log-file" /></CustomData>`（当前仅 `utf8` + `log-file`）。`@Name` 是真实物理文件名，改文件名不改引用。
- `Manifest/Injection` 声明无条件动作：`RequireDll` 仅校验存在性与哈希、不注入；`InjectDll` 注入并计入必需校验集合。凡清单声明了任何 Dll `ID` 或任何版本设置绑定（`Manifest/Settings`，含只挂包/挂语言），必须显式写出 `<Injection>`；无无条件动作时写空节点 `<Injection />` 作为新执行机制标记。未写 `<Injection>` 的历史清单完全沿用旧路由与全量基础校验，绝不把有绑定的清单当旧路线处理。
- `Manifest/Settings/SettingRef@Ref` 引用所属 Mod 的定义（禁止重复），子动作 `MountPackage@Name`、`MountLanguage`、`InjectDll@Ref`、`ConfigureLuaBridge@Ref/@Adapter` 与 `Case@Value` 有限值命中。`ConfigureLuaBridge` 目标必须是 `lyi-create-process` 协议且处于注入集合中。
- 具名 DTO：`ModSettingDefinition`、`ModSettingOption`、`ModSettingBinding`、`ModSettingAction`、`ModSettingCase`、`ManifestInjectionAction`、`ManifestDllCustomData`、`ModSettingsSnapshot`。`ModEntry.Settings`、`ManifestEntry.Settings`、`ManifestEntry.Injection`、`ManifestDllEntry.Id/Protocol/CustomData` 为新增 init 属性，保留原位置构造器。
- `ModSettingsContract` 提供 `ParseDefinitions`／`ParseBindings`／`SerializeSnapshot`／`ParseSnapshot`／`Validate`：快照 XML 为 `<SettingsSnapshot><Definitions>原定义</Definitions><Bindings>原绑定</Bindings></SettingsSnapshot>`，序列化与回读往返一致。

### 局部 ID、继承与校验

- 设置 ID 与 Dll ID 都是模组／清单内局部 ID，不参与 `{路径前缀}:{localId}` 资源 ID 规则；引用节点一律用 `Ref`，禁止用 `ID` 冒充定义。
- 继承合并：子同 ID 且同类型（`Boolean` 对 `Boolean`、`Choice` 对 `Choice`）原位覆盖父项并保留父项位置，新 ID 按声明顺序追加；类型冲突硬失败。`Base` 也支持 `Settings`。
- 构建期跨节点硬校验：ID 唯一、默认值／Case 命中 Option、`SettingRef` 唯一且存在、注入目标声明已知协议、`ConfigureLuaBridge` 协议与注入集合、`MountPackage` 命中 `Skudef` 的 Package、显式 `Injection` 缺失。设置文案 `@Language` 必须是主语言族（`zh`／`en` 等，不接受地区标签）。动作类型约束：`Boolean` 只能用 `MountPackage`／`InjectDll`／`ConfigureLuaBridge`，不能用 `MountLanguage`；`Choice` 根动作只允许 `MountLanguage`（枚举原值直传），其余动作必须写在 `Case` 中；`Case` 内禁止 `MountLanguage`；整个清单最多一个 `MountLanguage`。构建完整叶子清单时按所属 Mod 与所选版本叶子配对执行；分析设置定义/绑定时遇到未知节点直接报错，不静默忽略。
- `ConfigureLuaBridge` 只做协议／适配器结构校验，不声称构建期能验证二进制 ABI；实际 ABI 由 Desktop 既有原生链在运行时承担。

## Alternatives considered

- **内置模组能力名（如 `NativeDllCapability Name="ARCratesDll"`）**：把模组身份与 DLL 注入硬编码进核心模式与客户端枚举，任何新模组或新 DLL 都要改客户端代码，与通用化冲突，已废弃。现由 `<Dll ID Protocol>`、`RequireDll`／`InjectDll` 与 `Ref` 绑定取代。
- **在发布根 `Manifest` 存根里内联 `SettingRef`**：发布根的 `Manifest` 必须保持 `ID`+`Source` 存根，完整叶子单独输出；把版本绑定塞进存根会破坏存根与 `Manifest` 的对应关系。
- **安装阶段按用户选项校验并固化 DLL**：把用户选择刻进安装记录会破坏历史／离线一致性；校验推迟到单次启动的 `Prerequisites`，安装只固化快照。

## Consequences

- 新增设置、枚举、挂包与已支持协议的 DLL 只改数据；新的原生 ABI 或协议适配器仍需 Desktop 注册。
- 未声明设置的历史模组不强制新增 `Settings`，旧 `Package` 挂载规则不变。
- 发布根 `SchemaVersion` 保持 `1.0`：仅保证旧客户端仍能访问入口，不保证旧客户端会按默认值安全挂载（旧客户端可能把带条件的 `.big` 当作基础包全量挂载），该风险另行确认。

## Verification

- `dotnet test Ra3.BattleNet.Metadata.Tests/Ra3.BattleNet.Metadata.Tests.csproj -c Debug --verbosity minimal`：214 通过，0 失败；含定义解析、快照往返、引用/类型/协议/适配器/Case/`Injection` 硬失败、挂载设置缺少显式标记、类型动作约束与继承保序用例。
- CLI 真实构建：输出到隔离临时目录，生成 `metadata.xml` 与三份模组（Corona／ArmorRush／NeuroEva）叶子清单，`SchemaVersion="1.0"`，源 XSD 与发布 XSD 校验、展平与所属 Mod↔叶子配对校验全部通过。
- 原生注入的 ABI 与运行时行为不由单元测试覆盖，由 Desktop 既有原生链在运行时承担。
