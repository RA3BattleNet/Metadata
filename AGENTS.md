# AGENTS.md — Metadata 仓库指引

本仓库是**红警3战网**的元数据仓库，只承担两件事：

1. **数据仓库**：作者提交源树（XML + 图片/Markdown/清单），CI 构建后发布为静态文件，客户端下载解析；
2. **解析库**：`Ra3.BattleNet.Metadata`（C#，纯 managed）——客户端通过 **URL 或本地文件**读取发布物。

> 阅读指引：**人类作者**（写数据）看「数据贡献者指南」；**自动化 Agent** 请通读全文并按约束执行。
> 使用方契约见 [README.md](./README.md)。

## 常用命令

```bash
# 测试（MSTest）
dotnet test Metadata.sln

# 核心构建（默认；Desktop 本地调试同此）
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output

# 发布构建（核心 + WebP）
npm run build:release
# 或
bash build.sh --webp
```

- **不要**再引入 `RUN_STAGE_B` / `StageB` 控制面。
- 本地默认**不**跑 Imaging；Cloudflare Pages 构建用 `bash build.sh --webp`。

---

# 数据贡献者指南（写数据的人必读）

## 快速开始：写一个新 Mod（5 步）

1. 复制脚手架 `Metadata/Templates/Mod.xml` → `Metadata/mods/<你的Mod名>/<你的Mod名>.xml`
2. 改 ID：`<Mod ID="<你的Mod名>">`（**全仓库唯一**）；资源短 ID（`Image`/`Markdown`/`Manifest`）文件内唯一即可
3. 把图片/Markdown 放进同目录（如 `images/`、`posts/`），登记节点的 `Source` 指向它们
4. 在 `Metadata/mods/mods.xml` 的 `<Includes>` 里加一行：
   `<Include Source="<你的Mod名>/<你的Mod名>.xml" />`
5. 自检（见「提交前自检」）

写 Application 同理，目录换成 `apps/<应用名>/`，挂到 `apps/apps.xml`。

## 三条规则（其余交给构建器）

1. **文件路径唯一**：`mods/<Mod名>/` 或 `apps/<应用名>/`，路径即命名空间；
2. **资源短 ID 文件内唯一**：同一文件里的 `Image`/`Markdown`/`Manifest` `@ID` 不重复即可；
3. **实体短 ID 全局唯一**：`Mod`/`Application` 的 `@ID` 跨全仓唯一（大小写不敏感、跨类型）。

构建器负责：ID 限定（`{路径前缀}:{localId}`）、引用改写、资源复制、全部校验。
**冲突 = 构建失败**（报错带来源文件），绝不静默产出脏数据。禁止手写 `:` 前缀。

## 源树长什么样

```
Metadata/
  metadata.xml              入口：只挂 Include
  apps/<应用名>/            每个 Application 一个目录
    <应用名>.xml            实体定义 + 资源登记
    changelogs/ manifests/ posts/ ...
  mods/<Mod名>/             每个 Mod 一个目录
    <Mod名>.xml             实体定义 + 资源登记
    changelogs/ manifests/ posts/ images/ ...
  Templates/                脚手架（复制用，不参与构建）
```

## 一个 Mod 文件怎么写

```xml
<Metadata>
  <!-- 资源登记：文件内短 ID 即可，Source 指向同目录资源文件 -->
  <Image ID="icon-64px" Source="images/icon-64px.png" />
  <Markdown ID="news-zh-3229" Source="news-zh-3229.md" Hash="${MD5::}" />
  <Image ID="logo-example" Url="https://example.com/logo.png" />  <!-- 外链不落盘 -->

  <Mod ID="Corona">                              <!-- 实体 ID：全仓库唯一 -->
    <CurrentVersion>3.229</CurrentVersion>
    <Icon>icon-64px</Icon>                       <!-- 引用写短名 -->
    <Style>
      <Logo Width="400" Height="80">icon-64px</Logo>
      <Controls>
        <LaunchButton><BorderBrush>#FF000000</BorderBrush></LaunchButton>
      </Controls>
      <Background Random="true">
        <Image>background-1</Image>              <!-- Background 里的 Image 是引用，无 ID -->
      </Background>
    </Style>
    <Packages>
      <Package Version="3.229">
        <ReleaseDate>2025-04-01</ReleaseDate>
        <Changelogs>
          <Changelog Language="zh-CN">changelog-zh-3229</Changelog>
        </Changelogs>
        <Manifest>manifest-3229</Manifest>
      </Package>
    </Packages>
    <Posts>
      <Post DateTime="2025-04-01T00:00:00+08:00">
        <Titles><Title Language="zh-CN">版本更新 3.229</Title></Titles>
        <Contents><Content Language="zh-CN">news-zh-3229</Content></Contents>
      </Post>
    </Posts>
  </Mod>
</Metadata>
```

要点：

- **登记节点** = 带 `@ID` 的 `Image`/`Markdown`/`Manifest`；引用（Icon / Logo / Background 内 Image / Changelog / Content / Package.Manifest）写**短名**，构建器在「本文件 + 其 Include」作用域内解析并改写为限定 ID。
- `Include` 只写 `Source`（相对本文件），没有 `Type`/`Path`；子模块文件同样以 `<Metadata>` 为根。
- 需要共享骨架时可用 `Base`/`InheritFrom`（构建期合并，发布物无痕迹）；不需要就忽略。

## Manifest 两种写法（都合法，File 表都不内联进 metadata.xml）

1. **省事写法**：模块文件顶层直接写 `<Manifest ID="..."><File .../></Manifest>`——发布 stub 的 `Source` 指向**本模块文件**（示例/占位够用，叶子清单里会含实体定义，略冗余）；
2. **规范写法（推荐，corona 模式）**：`manifests/manifests.xml` Include 独立叶子文件 `manifests/<版本号>.xml`（只含 `Manifest` + `File` 表）——stub 指向独立叶子文件，**Hash 由 Updater 维护**，本仓示例 Hash 可为占位。

## 变量（构建时替换）

| 语法 | 含义 |
|---|---|
| `${TIMESTAMP}` | UTC 时间 |
| `${ENV:NAME}` | 环境变量 |
| `${MD5:}` / `${MD5::}` | 文件 MD5（`${MD5::}` 按登记节点 `Source` 计算） |

## 提交前自检

```bash
dotnet test Metadata.sln          # 全部通过
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
```

任何校验失败都是**硬失败**：不会产出半残发布物。常见错误：

```text
错误: 实体 ID 重复: "Corona"
  - mods/corona/corona.xml (Mod)
  - mods/fake/fake.xml   (Application)

错误: 登记 ID "a/b:icon" 含 ':'——前缀由构建器自动生成，请写短名

错误: Image 资源不存在: images/nope.png (ID: ...)
```
