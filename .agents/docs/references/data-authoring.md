# 写数据：Mod、应用、公告

面向手动写 XML 的作者。构建器负责补前缀、复制资源、校验冲突，你只需要把文件放对位置。

---

## 一、新增一个 Mod（5 步）

1. 复制 `Metadata/Templates/Mod.xml` 到 `Metadata/mods/<Mod英文名>/<Mod英文名>.xml`。
2. 把 `<Mod ID="…">` 改成你的名字，全仓库唯一（不分大小写，`Mod` 与 `Application` 之间也不能撞）。
3. 图片与 Markdown 放在同一目录下（例如 `images/`、`posts/`），XML 里资源的 `Source` 填相对路径。
4. 在 `Metadata/mods/mods.xml` 的 `<Includes>` 里加一行：`<Include Source="<Mod英文名>/<Mod英文名>.xml" />`。
5. 跑自检：`dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output`。

新增 Application 流程相同，目录换成 `Metadata/apps/<应用名>/`，挂到 `Metadata/apps/apps.xml`。

无论 Mod 还是 Application，`zh` 与 `en` 两条 `DisplayName` 都要改成真实产品名。

## 二、三条硬规则

1. **目录就是命名空间**。所有东西放在 `mods/<Mod名>/` 或 `apps/<应用名>/` 下，目录名独一无二。
2. **文件内的资源短 ID 只要本文件内不重名**。`Image`、`Markdown`、`Manifest` 的 `@ID` 不需要全仓库唯一。
3. **顶层实体的 `@ID` 全仓库唯一**。大小写不敏感，跨 Mod 与 Application 一起查重。

构建器自动做的事：补全 `{路径前缀}:{localId}`、把引用处的短名换成完整 ID、复制被引用资源、做全量校验。**不要自己手写冒号前缀**，写了会被直接拒绝。

## 三、目录结构

```text
Metadata/
  metadata.xml              总入口，只有 Include
  apps/<应用名>/apps.xml    应用列表入口
    <应用名>.xml            应用定义与资源登记
    manifests/ posts/ ...
  mods/<Mod名>/mods.xml     Mod 列表入口
    <Mod名>.xml             Mod 定义与资源登记
    manifests/ posts/ images/ ...
  Templates/                脚手架模板，不参与打包
```

`Include` 只写 `Source`，路径相对当前文件；所有子文件根节点都是 `<Metadata>`。

## 四、完整示例

```xml
<Metadata>
  <!-- 资源登记：短 ID + 相对路径 -->
  <Image ID="icon-64px" Source="images/icon-64px.png" />
  <Markdown ID="news-zh-3229" Source="news-zh-3229.md" />
  <Markdown ID="news-en-3229" Source="news-en-3229.md" />
  <Image ID="logo-example" Url="https://example.com/logo.png" />  <!-- 外链图，不复制到本地 -->

  <Mod ID="Corona">
    <CurrentVersion>3.229</CurrentVersion>
    <Icon>icon-64px</Icon>
    <DisplayName Language="zh">日冕</DisplayName>
    <DisplayName Language="en">Corona</DisplayName>
    <Style>
      <Logo Width="400" Height="80">icon-64px</Logo>
      <Background Random="true">
        <Image>icon-64px</Image>
      </Background>
    </Style>
    <Packages>
      <Package Version="3.229">
        <ReleaseDate>2025-04-01</ReleaseDate>
        <Manifest>manifest-3229</Manifest>
      </Package>
    </Packages>
    <Posts>
      <Post DateTime="2025-04-01T00:00:00+08:00">
        <Titles>
          <Title Language="zh">版本更新 3.229</Title>
          <Title Language="en">Update 3.229</Title>
        </Titles>
        <Descriptions>
          <Description Language="zh">简短介绍。</Description>
          <Description Language="en">Short summary.</Description>
        </Descriptions>
        <HeadImage>icon-64px</HeadImage>
        <Contents>
          <Content Language="zh">news-zh-3229</Content>
          <Content Language="en">news-en-3229</Content>
        </Contents>
      </Post>
    </Posts>
    <Links>
      <Link Url="https://example.com/" Languages="zh en">
        <Icon>icon-64px</Icon>
        <DisplayName Language="zh">官方网站</DisplayName>
        <DisplayName Language="en">Official website</DisplayName>
      </Link>
    </Links>
  </Mod>
</Metadata>
```

引用资源的地方（`Icon`、`Style/Logo`、`Style/Background/Image`、`Post/HeadImage`、`Post/Contents/Content`、`Link/Icon`、`Package/Manifest`）统一写短名。

## 五、几条容易踩的规则

- **`Posts` 属于实体**，不挂在 `Package` 上。没有新闻写 `<Posts/>`，不要编造，也不要拉一个公共列表。
- **`Post` 子元素顺序固定**：`Titles` → 可选 `Descriptions` → 可选 `HeadImage` → `Contents`。摘要省略时构建器不会从正文里截。
- **`HeadImage` 只能引用已登记的 `Image`**；指向 Markdown、Manifest 或不存在的 ID 都会硬失败。
- **`Links` 只能写在 `Mod` 下**，Application 不能有。`Link@Url` 只接受 HTTP／HTTPS，必须有主机，不能带空白。
- **`Base` 不能定义 `Posts`、`Links`、`DisplayName`**；这些属于实体自己，不继承。
- **正文外跳链接写在 Markdown 里**，不要给 `Post` 加 `Url` 或 `Version`。
- **`Changelogs` 已经删除**，没有兼容层。版本更新说明就是一条 `Post`。

显示名、语言标签、样式字段、启动兼容性的完整规则见 [metadata-model.md](metadata-model.md)。

## 六、构建时替换的宏

| 写法 | 替换成 |
|---|---|
| `${TIMESTAMP}` | 构建时刻的 UTC 时间戳 |
| `${ENV:NAME}` | 环境变量 `NAME` 的值 |

只有 `Manifest/File@Hash`、`Dependencies/Dll@Hash`、`UpdateKind/Updater@Hash` 带哈希，由各自的生成方给出；`Image` 与 `Markdown` 不写 `Hash`，源 XSD 会拒绝。

## 七、报错怎么读

构建器是硬失败：报错退出、清空 `Output/`、进程返回 1。

```text
错误: 实体 ID 重复: "Corona"
  - mods/corona/corona.xml (Mod)
  - mods/fake/fake.xml   (Application)

错误: 登记 ID "a/b:icon" 包含 ':'——前缀由构建器自动生成，请直接写短名

错误: Image 资源不存在: images/nope.png (ID: ...)

错误: Mod 'Demo': 缺少 en DisplayName
错误: Application 'RA3BattleNet': DisplayName Language 重复: zh
```

改完重跑，直到输出 `✓ 构建完成`。
