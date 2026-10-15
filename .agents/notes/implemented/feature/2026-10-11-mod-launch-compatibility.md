# Agent Note: Mod 附属身份与版本兼容声明

Status: implemented

## Problem

Desktop 启动集需要知道哪些内容能够作为附属加载，及是否能够与选定主项或其他附属共同使用。现有 Mod 查询没有此信息。

## Decision

Mod@IsAddon 独立表示附属身份，缺省 false；任何 Mod 可以作为主项。Compatibility/Main 是允许的主 ID 列表，Compatibility/Addon 是附属兼容例外（Compatible 缺省 true）。附属默认兼容原版、默认不兼容其他主 Mod、默认兼容其他附属。Version 子元素是精确版本提示范围，未列出目标实际版本时只警告，不改变 ID 层的允许/拒绝。

Package/Compatibility 完整覆盖 Mod 默认规则，缺省沿用；IsAddon 不随版本改变。引用为全局 Mod ID，构建器不加资源前缀，大小写不敏感；构建器拒绝未知、自引用、重复目标和非附属 Addon 目标。SchemaVersion 保持 1.0，旧数据采用默认规则。

## Alternatives considered

- 用兼容列表推导附属身份：字段少，但用户要求独立身份，以允许附属只兼容原版。
- 版本不匹配禁止启动：可严格保护作者声明的支持范围，但用户要求版本只警告。
- 版本规则逐项合并：修改少，但无法通过空 Compatibility 清空继承；完整覆盖使每版生效规则明确。

## Consequences

旧 Mod 默认仍只作主项。数据作者需要声明附属身份和兼容关系，客户端不能证明附属文件没有联机逻辑变化。规则可在版本间演进，但启动前必须重新验证；客户端版本提示不提供语义版本范围语法。

## Related decision audit

已有 Mod 样式、显示名及 skudef 决定部分重叠：沿用实体 ID、发布模式与版本包，不改变资源引用和挂载指令。新兼容数据不从 Base 继承。

## Verification

核心构建通过源模式与发布模式校验；兼容性测试覆盖旧默认、包替换与空规则清除、继承后的规则保留、全局引用、重复目标、版本清单、Base/Application 限制及 XML boolean 空白归一化。数据构建保持 SchemaVersion 1.0，无需更改既有 Mod 数据。
