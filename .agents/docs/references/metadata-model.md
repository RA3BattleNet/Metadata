# 元数据模型

XML 的字段、默认值和校验规则。字段顺序以 [MetadataSchema.xsd](../../../Metadata/MetadataSchema.xsd) 为准；本文说明字段的含义和取舍。

---

## 一、根节点

| 属性 | 含义 |
|---|---|
| `SchemaVersion` | 数据契约版本，当前固定 `1.0`。客户端用 `MetadataSchema.IsCompatible` 判断能否读取 |
| `ContentRevision` | 构建时写入的提交标识，客户端据此判断内容是否更新 |

`SchemaVersion` 提高意味着旧客户端可能无法工作。改样式、加可选字段、改默认值都不提高版本。协议版本是 `1.0` 不等于所有旧字段都还在：`Package.Changelogs` 和 `ChangelogSource` 已经删除，没有兼容层，调用方改读实体自己的 `Posts`。

## 二、实体与版本包

| 元素 | 位置 | 必填 | 说明 |
|---|---|---|---|
| `Application` | 根子元素 | — | 应用：`@ID`、`Version`、显示名、`Packages`、`Posts`、可选 `TransferAd` |
| `Mod` | 根子元素 | — | Mod：`@ID`、`CurrentVersion`、`Icon`、显示名、`Style`、`Packages`、`Posts`、`Links`、可选 `IsAddon` |
| `Package` | 实体子元素 | — | 版本包：`@Version` 必填，可选 `ReleaseDate`、`Manifest`、`Compatibility` |
| `Post` | 实体 `Posts` 子元素 | — | 文章：可选 `@Type`（`introduction` 表示介绍、`changelog` 表示版本更新、省略即普通新闻）、`@DateTime`、`Titles`、可选 `Descriptions`、可选 `HeadImage`、`Contents` |
| `Link` | 仅 `Mod` 子元素 | — | 友情链接：`@Url`（只接受 HTTP／HTTPS）、可选 `@Languages`、可选 `Icon`、至少一条 `DisplayName`；顺序即展示顺序 |

- `Package` 只保留安装数据，不带更新日志。
- `Application` 必须有 `Posts` 节点（没有新闻就写 `<Posts/>`），不能有 `Links`。
- `Post` 里外跳链接写在 Markdown 正文，`Post` 没有 `Url`、`Version` 属性。
- `Type` 是自由字符串，构建器不限制取值；客户端只对 `introduction` 和 `changelog` 有已知处理。

## 三、资源登记

带 `@ID` 的 `Image`、`Markdown`、`Manifest` 是登记节点：

| 节点 | 正文来源 | 说明 |
|---|---|---|
| `Image` | `Source` 相对路径，或 `Url` 外部直链 | 外链图不复制进发布树；正式发版可能转成 `.webp` |
| `Markdown` | `Source` 相对路径 | 构建器只检查文件存在，不解析正文 |
| `Manifest` | `Source` 相对路径 | 在大总表里只是占位引用，正文单独发布 |

引用它们时写短名。展平后所有引用都变成 `{路径前缀}:{短名}`，客户端按完整 ID 查登记节点，再拼 `Source` 得到真实地址。

## 四、显示名与语言

- `Mod` 与 `Application` 都通过重复的 `<DisplayName Language="…">文本</DisplayName>` 声明显示名。
- **必须各写一条 `zh` 与 `en`**，完整标签精确匹配、比较不分大小写；可以再补其他语言。
- 同一实体内完整标签不能重复，文本不能为空。
- 显示名属于实体自身，不从 `Base` 继承；`Base` 也不允许写 `DisplayName`。
- `UpdateKind@DisplayName` 的契约不变，仍由客户端自行覆盖展示。

语言比较统一走 `LanguageTag.Matches` / `LanguageTag.Select`：

| 数据侧写法 | 查询 `zh` | 查询 `zh-CN` |
|---|---|---|
| `zh` | 命中 | 命中（按语言族） |
| `zh-CN` | 不命中 | 命中 |
| `english` | 不命中 | 不命中 |

数据侧只写 `zh` / `en`。`zh-CN`、`en-US` 不能代替实体必填的 `zh` / `en`，构建器不会替你改写标签。

## 五、Mod 页面样式

`Mod/Style` 分 `Logo`、`Controls`、`Background` 三块，所有字段可省略；整块 `Style` 省略时客户端用主题默认值。

- `Logo`：文本是图片 ID，可选 `Width` / `Height`、九宫格 `Position`、`OffsetX` / `OffsetY`。只写一边尺寸时保持比例。
- `Controls`：`PrimaryLabel` / `SecondaryLabel` / `PrimaryButton` / `SecondaryButton`。文字字段 `Color`、`FontSize`、`FontWeight`；按钮另有 `BorderColor`、`BorderWidth`、`BackgroundColor`。
- 按钮的 `Hover` / `Active` 只覆盖自己声明的字段，其余取合并后的普通样式。`Active` 表示按下，不是选中。
- `Background`：`Image` 列表加 `Random`；`Color` 是底色，`SecondaryColor` 是卡片色。半透明底色会露出背景图。
- 颜色节点可写 `Format="CSS"` 或 `Format="ARGB"`。省略按 `#RRGGBB` / `#RRGGBBAA` 解析；ARGB 必须 `#AARRGGBB`。颜色值与 Format 一起参与继承替换。
- 尺寸、字号为正数，`BorderWidth` 可为 0，单位逻辑像素；`FontWeight` 是 100～900 的整数。显式 0 与缺失不同。
- 继承规则：控件与 `Hover` / `Active` 按字段合并；`Logo` 与 `Background` 是整块替换——子 Mod 写了这一块就完全替换，不写才继承 `Base`。

读取示例：

```csharp
var color = mod.Raw.Find("Style:Controls:PrimaryButton:BackgroundColor");
var css = color is null ? null : MetadataColor.ToCss(color.Value!, color.Get("Format"));
```

XML 用 PascalCase，Vue 对象用 camelCase，CSS 属性用 kebab-case。尺寸在 Vue `:style` 里要转成 `px` 字符串，字重没有单位。本仓库只提供数据，不含 Vue 页面实现。

## 六、Mod 启动兼容性

`Mod@IsAddon="true"` 表示这是不改变联机逻辑的附属内容，缺省 `false`。任何 Mod 都能当主 Mod；附属按原版身份启动，显式“作为主 Mod 启动”时仍走常规 Mod 身份。

```xml
<Mod ID="VisualAddon" IsAddon="true">
  <Compatibility>
    <Main Mod="Corona"><Version>3.258</Version></Main>
    <Addon Mod="OtherVisualAddon" Compatible="false"/>
  </Compatibility>
  <Packages>
    <Package Version="1.0">
      <Manifest>manifest-100</Manifest>
      <Compatibility/>
    </Package>
  </Packages>
</Mod>
```

- `Compatibility` 在 `Mod` 下位于 `Style` 之后、`Packages` 之前；在 `Package` 下位于 `Manifest` 之后。
- `Main` 是允许的主 Mod；不声明时默认不兼容任何主 Mod。`Addon` 是例外列表，`Compatible="false"` 表示拒绝该组合。
- 附属默认兼容原版和其他附属。
- `Version` 列出目标支持的精确版本；不匹配只提示，不阻止启动。
- `Package` 里的 `Compatibility` 完整替换 Mod 默认规则，空节点表示清空；省略则沿用。
- 兼容 ID 大小写不敏感，不加资源前缀，不能引用自身、未知目标或重复目标；`Addon` 目标必须自己声明 `IsAddon`。
- `vanilla` 留给原版，不能当 Mod ID。
- 这些规则不从 `Base` 继承。

库通过 `mod.IsAddon` 与 `mod.LaunchCompatibility(version)` 暴露结果。声明是作者承诺，构建器无法证明文件真的没改联机逻辑。

历史决策见 [Mod 样式](../../notes/implemented/feature/2026-10-04-mod-style.md)、[必填显示名](../../notes/implemented/feature/2026-10-05-required-displayname.md)、[语言标签](../../notes/implemented/feature/2026-10-09-unified-language-tags.md)、[启动兼容性](../../notes/implemented/feature/2026-10-11-mod-launch-compatibility.md)。
