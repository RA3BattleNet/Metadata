# AGENTS.md — Metadata 仓库指引

本仓库是**红警3战网**的元数据仓库，核心其实就管两件事：

1. **放数据的地方**：Mod 作者或者开发者提交原始的 XML 文件、图片、更新说明 Markdown 和安装清单，GitHub CI 编译打包后发布成静态网页文件，客户端联网下载并解析；
2. **读数据的解析库**：`Ra3.BattleNet.Metadata`（纯 C# 编写，不依赖任何第三方平台组件）——客户端不管是通过**网页链接（URL）还是本地文件夹**，都用这个库来读取和解析发布出来的数据。

> 怎么看这份文档：**自己手动写数据的作者**看下文的「数据贡献者指南」；**自动化 Agent 或开发人员**请通读全文并严格按规矩执行。
> 客户端怎么调用解析库的用法，见 [README.md](./README.md)。

## 常用开发命令

```bash
# 运行单元测试（使用 MSTest）
dotnet test Metadata.sln

# 核心编译打包（平时开发默认跑这个，本地调试桌面端也是这个）
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output

# 正式发布打包（包含把图片转成 WebP 格式）
npm run build:release
# 或者在 Linux / Git Bash 下跑：
bash build.sh --webp
```

- **千万不要**在代码里再去加老旧的 `RUN_STAGE_B` 或 `StageB` 参数控制面。
- 本地开发测试时默认**不会**去跑耗时的图片转换（Imaging）；部署到 Cloudflare Pages 线上生产环境时才执行 `bash build.sh --webp`。

---

# 数据贡献者指南（写数据的人必读）

## 快速上手：怎么添加一个新的 Mod（只需 5 步）

1. 复制现成的模板脚手架：把 `Metadata/Templates/Mod.xml` 复制一份到 `Metadata/mods/<你的Mod英文名>/<你的Mod英文名>.xml`；
2. 改唯一标识：把里面的 `<Mod ID="<你的Mod英文名>">` 改成你的名字（**必须全仓库唯一**）；至于里面的图片、Markdown、清单等资源短 ID，只要在当前文件内部不重名就行；
3. 把你要用到的图片和 Markdown 说明文件放进同一个文件夹里（比如建个 `images/` 或 `posts/` 目录），XML 里各个资源的 `Source` 属性直接填写它们的相对路径；
4. 打开 `Metadata/mods/mods.xml`，在 `<Includes>` 列表里加上一行，把你的文件挂上去：
   `<Include Source="<你的Mod英文名>/<你的Mod英文名>.xml" />`
5. 运行自检命令（见下文「提交前自检」），检查有没有格式错误。

如果要添加 Application（比如战网客户端自身或者游戏运行资源），流程完全一样，只是把目录换成 `apps/<应用名>/`，并挂载到 `apps/apps.xml` 里。

## 必须遵守的三条铁律（剩下的繁琐杂活全部交给构建器处理）

1. **文件夹路径必须独一无二**：所有东西老老实实放在 `mods/<Mod名>/` 或 `apps/<应用名>/` 目录下，文件在哪个目录，就相当于在哪个命名空间里；
2. **文件内的资源短 ID 只要文件内不重复即可**：同一个 XML 文件里的 `Image`、`Markdown`、`Manifest` 的 `@ID` 只要在当前文件里不重名就没问题；
3. **顶层实体的全局 ID 必须全仓库唯一**：每个 `Mod` 或者 `Application` 的 `@ID` 必须在整个仓库里独一无二（不区分大小写，跨 Mod 和 Application 统统不能撞名）。

构建打包程序会自动帮你做：补全完整的全名 ID（格式为 `{路径前缀}:{localId}`）、自动替换互相引用的地方、自动把引用的图片等资源拷贝到输出目录，并进行全方位校验。
**只要发现任何冲突，打包就会立刻硬失败报错**（报错信息会明确指出版权在哪一行、哪个文件），坚决不偷偷摸摸产出脏数据。平时在写 XML 时，**坚决禁止自己手动去拼冒号 `:` 前缀**。

## 原始数据目录长什么样

```text
Metadata/
  metadata.xml              整个项目的总入口：里面只负责用 Include 挂载其他文件
  apps/<应用名>/            每个独立应用程序放一个目录
    <应用名>.xml            定义应用本身以及登记所需的资源
    changelogs/ manifests/ posts/ ...
  mods/<Mod名>/             每个 Mod 放一个目录
    <Mod名>.xml             定义 Mod 本身、版本、外观样式以及登记所需资源
    changelogs/ manifests/ posts/ images/ ...
  Templates/                脚手架模板（新建文件时直接复制，不参与正式打包）
```

## 一个完整的 Mod 文件到底怎么写

```xml
<Metadata>
  <!-- 登记你要用到的资源：写文件内部的短 ID 即可，Source 属性指向同目录下的实际文件 -->
  <Image ID="icon-64px" Source="images/icon-64px.png" />
  <Markdown ID="news-zh-3229" Source="news-zh-3229.md" Hash="${MD5::}" />
  <Image ID="logo-example" Url="https://example.com/logo.png" />  <!-- 外部图片直链，不会下载保存到本地 -->

  <Mod ID="Corona">                              <!-- 实体 ID：在整个仓库里必须是唯一的 -->
    <CurrentVersion>3.229</CurrentVersion>
    <Icon>icon-64px</Icon>                       <!-- 引用上面登记的资源，直接写短名 -->
    <DisplayName Language="zh-CN">日冕</DisplayName>   <!-- 可选：支持按语言配置显示名，可以写多条 -->
    <Style>
      <Logo Width="400" Height="80">icon-64px</Logo>
      <Controls>
        <PrimaryButton>
          <BorderColor Format="ARGB">#FF000000</BorderColor>
          <Hover><BackgroundColor>#FFFFFF</BackgroundColor></Hover>
          <Active><BackgroundColor>#CCCCCC</BackgroundColor></Active>
        </PrimaryButton>
      </Controls>
      <Background Random="true">
        <Image>icon-64px</Image>                 <!-- 背景里的 Image 纯粹是引用短名，不需要写 ID -->
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

核心要点说明：

- **资源登记节点**：指的是带 `@ID` 属性的 `Image`、`Markdown`、`Manifest` 标签；而在后面引用它们时（比如 Icon、Logo、Background 里的 Image、更新日志 Changelog、公告内容 Content、版本包对应的 Package.Manifest），**统一直接写短名字**，构建打包程序会自动在“当前文件以及它所引入的文件”作用域内，帮你把短名字改写成带路径前缀的完整 ID。
- `Include` 标签只需要写 `Source` 属性（填相对于当前文件的路径），不需要写额外的 `Type` 或 `Path` 属性；所有子文件根节点一律以 `<Metadata>` 开头。
- 如果多个 Mod 想共享同一套公共样式，可以使用 `Base` 和 `InheritFrom` 继承机制（编译打包时会自动合并，最终发布出来的文件里不留痕迹）；平时如果用不到直接忽略即可。
- `Style` 和所有样式字段都可省略，未配置时由客户端主题决定。标签分 `PrimaryLabel` / `SecondaryLabel`，按钮分 `PrimaryButton` / `SecondaryButton`；按钮的 `Hover` 与 `Active` 分别表示悬停和按下，状态字段缺省时使用最终合并后的普通按钮字段，不从 Hover 回退到 Active。
- 颜色字段使用 `Color` / `BackgroundColor` / `BorderColor` / `SecondaryColor`，每个颜色节点可写 `Format="CSS"` 或 `Format="ARGB"`。省略时按 CSS 的 `#RRGGBB` / `#RRGGBBAA` 解析；ARGB 必须是 `#AARRGGBB`。颜色值和 Format 在继承时一起替换。客户端可调用 `MetadataColor.ToCss(value, format)` 转成 CSS。
- 字号用 `FontSize`（正数），字重用 `FontWeight`（100～900 的整数），边框宽度用 `BorderWidth`（非负数）。Logo 尺寸、字号、边框与偏移单位为逻辑像素。详细属性与 Vue 映射见 [Mod 样式文档](.agents/notes/implemented/feature/2026-10-04-mod-style.md)。
- 元数据契约版本保持 `1.0`，Style 调整不提高整个元数据的版本号。客户端可调用 `MetadataSchema.IsCompatible(root.Get("SchemaVersion"))` 检查；通过版本检查不代表已实现新样式展示。旧控件名称不再受 XSD 支持。

## 安装清单（Manifest）的两种写法（两种都合法，文件表都不会塞进大总表里）

1. **偷懒省事写法**：在 Mod 的主 XML 文件顶层直接写 `<Manifest ID="..."><File .../></Manifest>` —— 构建器生成的占位引用 `Source` 会直接指向**本模块文件**（平时做示例或本地测试够用，缺点是这个清单文件里顺带包含了 Mod 的实体定义，稍微多占了点体积）；
2. **正规规范写法（官方推荐，日冕 Mod 采用的就是这种）**：在 `manifests/manifests.xml` 里通过 Include 引入独立的叶子清单文件 `manifests/<版本号>.xml`（这个独立文件里只老老实实放 `Manifest`、`Skudef` 和 `File` 文件表）——打包后的引用精确指向该独立文件，**文件的哈希通常由 Updater 负责维护**，本仓库里的示例哈希可以先填占位。

叶子清单里的 `<Skudef>` 标签，决定了客户端启动游戏前生成的 skudef 脚本里该写哪些行、按什么顺序写（你在 XML 里写的子元素先后顺序，就是最终生成的行顺序）：

```xml
<Manifest ID="manifest-3258" HashAlgorithm="CRC32C">
  <Skudef GameVersion="1.12">
    <AddConfig LocalFile="CustomConfig.txt" Optional="true" />
    <AddBig File="Cor_ENG_3.250.big" Language="en" />
    <AddBig File="corona_3.258.lyi" />
  </Skudef>
  <File Hash="95DC8BF4" ...>
    <FileName>Cor_ENG_3.250.big</FileName>
    ...
  </File>
</Manifest>
```

- `AddBig@File` 必须填写对应的 `FileName`，而且每个 `FileName` 必须恰好被引用一次；`AddConfig@LocalFile` 填写用户在 Mod 目录下自己手动放的纯文件名。
- 加载条件二选一：`@Language`（只有当玩家客户端切换到该语言时才加载）、`@Package`（只有当玩家在客户端里勾选了该可选功能包时才加载）；如果什么都不写，代表无条件默认加载。
- 只要写了 `<Skudef>` 标签，就坚决不允许再跑到下面的 `<File>` 标签上写 `Mount`、`Language`、`Package` 属性（所有加载规则必须集中在 Skudef 这一处说明）。

## 构建时自动替换的宏变量

| 变量语法 | 实际替换成什么 |
|---|---|
| `${TIMESTAMP}` | 当前构建的 UTC 时间戳 |
| `${ENV:NAME}` | 读取名为 NAME 的环境变量 |
| `${MD5:}` / `${MD5::}` | 自动计算指定文件的 MD5 哈希（`${MD5::}` 会自动根据当前资源登记节点的 `Source` 文件去算） |

## 提交代码前的本地自检

在把代码推送到 Git 之前，务必在本地终端跑一遍自检：

```bash
dotnet test Metadata.sln          # 确保所有单元测试全部绿灯通过
dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output
```

任何一项校验没通过，构建器都会**直接报错退出**，坚决不产出半截残缺的坏数据。最常见的报错包括：

```text
错误: 实体 ID 重复: "Corona"
  - mods/corona/corona.xml (Mod)
  - mods/fake/fake.xml   (Application)

错误: 登记 ID "a/b:icon" 包含 ':'——前缀由构建器自动生成，请直接写短名

错误: Image 资源不存在: images/nope.png (ID: ...)
```
