# 红警3战网元数据仓库

本仓库是战网客户端的元数据中心，核心只承担两项职责：**数据仓库**（Mod 作者与开发者提交原始的 XML 与资源文件，CI 构建打包后发布为静态网页文件）+ **解析库**（`Ra3.BattleNet.Metadata`，纯 C# 编写，客户端通过网络 URL 或本地目录来读取和解析发布出来的数据）。

- **如何写数据（新增或修改 Mod、应用程序、更新公告）**：请直接阅读 [AGENTS.md「数据贡献者指南」](./AGENTS.md) —— 5 步快速上手、三条必须遵守的铁律、现成模板以及提交前自检命令都在那里。

## 发布出来的产物长什么样

构建打包程序最终产出的是纯粹的静态文件目录树，不是什么复杂的 RPC 接口服务：

- `{BaseUrl}/metadata.xml` —— 经过展平合并后的所有业务数据大总表（**已经移除了所有 Include 标签**）；
- `{BaseUrl}/{相对路径}` —— 各类配套资源文件（包括图片、Markdown 说明、各版本的独立清单），文件路径来自资源登记节点的 `Source` 属性；
- 根节点的核心属性：`SchemaVersion`（数据协议版本号）、`ContentRevision`（本次内容更新的修订号，客户端用来快速判断要不要拉取新缓存）。

## 客户端平时怎么读取和解析（以 Desktop 为例）

1. 配置 `BaseUrl` 地址（生产环境填 CDN 网页链接；开发调试时直接填本地生成的缓存目录路径）；
2. 调用 `MetadataBuilder.Load(url|path)` 读取数据 → 获取各个实体对象，直接支持 C# LINQ 链式查询（详见下表 API）；
3. 检查数据协议兼容性：如果 `SchemaVersion` 不兼容，提示用户升级客户端（调用 `MetadataSchema.IsCompatible`）；如果 `ContentRevision` 变了，说明服务器数据有更新，重新拉取整棵树；
4. 查找资源引用：版本清单 Package.Manifest、图标 Icon、更新日志 Changelog、公告内容 Post.Content 等，在展平后全都是**带前缀的完整 ID**（格式形如 `路径前缀:localId`），直接通过这个完整 ID 去大总表里查找对应的登记节点即可 —— 千万别自己写循环去傻傻遍历 XML 节点树，直接调库里封装好的导航方法；
5. 拼接资源的真实下载地址：真实地址 = `BaseUrl` + 登记节点的 `Source` 相对路径（调用 `MetadataResourceUri.Resolve` 方法即可安全拼装）；图片的最终文件后缀名以 `Source` 为准（经过正式发布后可能被转成了 `.webp` 格式）；
6. 读取各版本的独立清单：各个具体版本的详细文件表（File 列表）并没有塞进大总表 `metadata.xml` 里，而是需要根据清单的 `Source` 地址再去拉取一次独立的叶子清单文件并解析；至于游戏启动前怎么挂载文件，交给 `ManifestMountPlanner` 去规划即可。

**本地开发调试与线上生产环境完全走同一套解析代码**：`MetadataBuilder.Build(本地源数据目录, 输出目录)` → `Load(输出目录/metadata.xml)`。

```csharp
// 生产环境传网络 URL，开发环境传本地生成的文件路径
var doc = MetadataBuilder.Load("https://metadata.ra3battle.net/metadata.xml");

// 列出所有配置好的 Mod：返回的是延迟加载的序列，直接无缝对接 LINQ
foreach (var mod in doc.Mods().Where(m => m.Version is not null).OrderBy(m => m.Id))
{
    Console.WriteLine($"{mod.Id}@{mod.Version}");
}
var app = doc.Catalog().Application("RA3BattleNet");   // 按 ID 查找，内部自动忽略大小写

// 顺藤摸瓜：从 Mod 条目找到对应版本的独立清单地址，再拉取解析独立清单
var corona = doc.Catalog().Mod("Corona")!;
var leafUrl = MetadataResourceUri.Resolve("https://metadata.ra3battle.net/metadata.xml", corona.ManifestSource());
var manifest = MetadataBuilder.Load(leafUrl.AbsoluteUri).Find("Manifest")!.ToManifestEntry();

// 查找应用更新日志对应的相对路径文件（找不到对应语言返回 null）
var changelog = app!.ChangelogSource("zh-CN");

// 生成启动挂载计划：如果清单里写了 Skudef 就按声明顺序和条件来，没写就按老旧的 File@Mount 角色来
var plan = ManifestMountPlanner.Build(
    manifest.Skudef,
    manifest.Files.Select(f => new ManifestMountFile(f.FileName, f.Mount, f.Language, f.Package)).ToList(),
    language: "en",
    packages: ["hd-shadow"],
    localConfigExists: _ => false);
```

### 常用查询与导航 API 清单

| API 方法名 | 具体作用与说明 |
|---|---|
| `root.Mods()` / `Applications()` / `Markdowns()` / `Images()` | 列出所有的实体或资源；采用延迟计算，可直接链式调用 `Where` / `OrderBy` / `Select` / `First` |
| `root.Catalog()` | 快捷目录入口：包含 `Mods` / `Applications` / `Markdowns` / `Images` 属性，支持 `Mod(id)` / `Application(id)` 快速按 ID 查（不区分大小写） |
| `root.GetAllElements(name)` | 按 XML 标签名遍历整棵展平树（延迟枚举） |
| `root.ManifestRegistration(id)` | 根据完整 ID 查找 Manifest 清单登记节点；找不到返回 null |
| `ModEntry.Package(version)` / `ManifestSource(version)` | 获取指定版本的包定义（不传参数默认取当前最新版）/ 获取独立清单的相对 Source 路径；如果版本缺失、清单缺失或路径缺失都会抛出带明确原因的异常 |
| `ApplicationEntry.Package(version)` / `ChangelogSource(language)` | 获取指定版本的应用包 / 获取指定语言更新日志的相对 Source 路径；没配该语言时返回 null |
| `node.ToManifestEntry()` | 把 XML 节点解析转换成强类型的 `ManifestEntry` 对象（包含 Files 文件表、Dependencies 依赖、Skudef 挂载声明） |
| `MetadataSchema.Current` / `IsCompatible(version)` | 当前数据协议版本常量，以及判断某个版本是否向下兼容的方法 |
| `MetadataResourceUri.Resolve(baseUrl, source)` | 将相对 Source 路径安全拼装为完整的绝对网络地址或本地绝对路径 |
| `ManifestMountPlanner.Build(...)` | 根据 `Skudef` 声明或老旧的 `File@Mount` 属性，计算出有条不紊的挂载指令集 `ManifestMountCommand`（包括挂载 `Big` 包或生成用户 `Config` 挂载） |

核心编译打包命令（供贡献者、CI 机器人或桌面端本地调试调用）：

```csharp
MetadataBuilder.Build(sourceDir, outputDir, schemaVersion: "1.0", contentRevision: gitSha);
```

核心解析库本身是纯粹的托管代码，**完全不引入 SkiaSharp 这种重型图像库**；图片向 WebP 格式的转换压缩，只在正式发版流水线上的独立工具 `Ra3.BattleNet.Metadata.Imaging` 里执行。

### 校验规则与报错处理

编译构建过程采用严格的**硬失败机制**（一旦报错立刻返回非 0 退出码，并自动清空半吊子残缺输出）：
- 检查原始 XML 是否符合 `MetadataSchema.xsd`；
- 检查打包产物是否符合 `MetadataPublishSchema.xsd`；
- 检查是否存在循环 Include 互相引用；
- 检查所有引用的图片、Markdown、清单文件是否真实存在；
- 检查 ID 引用是否发生断链（引用的 ID 找不到）；
- 检查是否有未被替换的残留宏变量 `${...}`；
- 对新格式清单里的各项属性做严密语义检查。

### 独立版本清单文件（叶子 Manifest）规范

叶子清单（Manifest）里的 File 文件列表，支持明确声明下载来源以及哈希算法（新版格式）；未声明新字段的历史老清单依然能够向下兼容正常读取。

| 节点或属性 | 所在位置 | 详细说明 |
|---|---|---|
| `Manifest@HashAlgorithm` | Manifest 属性（可选） | 声明所用的哈希算法：支持 `CRC32C` / `MD5` / `SHA256`，默认缺省为 `CRC32C` |
| `File@Hash` | File 属性（必填） | **解压安装之后**最终游戏文件（`FileName`）的哈希校验值，算法由上面的 `HashAlgorithm` 决定 |
| `File@Size` | File 属性（可选） | 从网络上下载回来的**网络压缩包**大小（字节数，必须是正整数） |
| `File@DownloadName` | File 属性（可选） | 服务器上存放的压缩包文件名；如果不写，默认与 `FileName` 相同 |
| `File@Compression` | File 属性（可选） | 压缩包的压缩算法，目前仅支持 `zstd`；下载完校验无误后解压成正式的 `FileName` |
| `Sources/Source` | File 的子标签（可选） | 声明下载地址：包含协议类型 `@Type`（`HTTP` 或 `BT`）与下载链接 `@Url`；HTTP 必须是完整的 http/https 绝对地址，BT 必须以 `.torrent` 结尾 |
| `Dependencies/Dll` | Manifest 子标签（可选） | 声明该 Mod 依赖的第三方 DLL：`@Name` 和 `@Hash` 必填，`@Version` 和 `@KindOf` 可选 |
| `Skudef` | Manifest 子标签（可选） | 启动脚本生成规则，必须写在所有的 `File` 标签之前；`@GameVersion` 缺省默认为 `1.12` |
| `Skudef/AddBig` | Skudef 的子标签 | 挂载 big 包：`@File` 必填（必须引用当前清单里声明过的某个 `FileName`），配上可选的触发条件 `@Language` 或 `@Package`（两个条件只能写一个）；不写条件表示默认始终挂载 |
| `Skudef/AddConfig` | Skudef 的子标签 | 挂载用户配置文件：`@LocalFile` 必填（必须是存放在 Mod 目录下的纯文件名），`@Optional`（可选，默认为 false，如果找不到该文件就报错） |

`Skudef` 标签是客户端生成启动脚本时“该写哪些行、按什么顺序写”的**唯一权威来源**：客户端会老老实实按照 XML 里子标签的书写顺序生成对应的 `add-big` / `add-config` 行。只有当 `@Language` 与当前玩家设置的语言一致、或者玩家在客户端里勾选了对应的 `@Package` 可选包时，对应的挂载行才会生效。**只要在清单里写了 `<Skudef>` 标签，下面的 `<File>` 标签上就坚决不允许再写 `Mount`、`Language`、`Package` 属性**，而且清单里的每一个 `File` 都必须被恰好一条 `AddBig` 引用到。

特别注意：`File@Hash` 表达的是**解压后**正式游戏文件（`FileName`）的校验值；而 `File@Size` 表达的是**从网上下载时**压缩包文件（`DownloadName`）的大小：
服务器上通常存放压缩后的文件（例如 `corona_3.258.zst`），解压安装后的正式文件名是 `corona_3.258.lyi`，两者通过 `DownloadName` + `Compression` 来明确区分。客户端把文件下回来之后先进行解压，再对照 `@Hash` 核验解压出来的结果。

如果是直接以原样形式分发、未经过压缩的文件（比如启动器用的 `Disabler.big`），不需要填写 `DownloadName` 和 `Compression` 属性，此时 `@Size` 和 `@Hash` 描述的就是该文件本身。

一旦清单里使用了新版格式（比如标了 `HashAlgorithm`、写了 `Skudef`，或者任何一个文件配了 `Sources`），在编译打包阶段会自动开启严密的硬校验：哈希长度与算法必须匹配、坚决禁止留占位假哈希、每个 File 至少要有一个可用的下载源、文件大小必须是正整数、同清单内 `FileName + RelativePath` 必须唯一、依赖项 `Dll@Name` 不能重名、`RelativePath` 必须是标准的相对路径、`DownloadName` 必须是不含任何路径分隔符的纯文件名、配置了 `Compression="zstd"` 时必须同时配 `DownloadName`（且不能与 `FileName` 相同）。对于历史老清单，则不触发上述严格拦截。

写了 `Skudef` 的清单还会额外再多查一道：`GameVersion` 格式形如 `1.12`、内部至少包含一条挂载指令、文件名全清单唯一、每一条 `AddBig@File` 必须指向真实存在的 `FileName` 且只能指向一次、所有声明的文件都必须被挂载引用到、`Language` 和 `Package` 条件只能写一个且只能包含字母数字下划线连字符、`AddConfig@LocalFile` 必须是纯文件名、`File` 标签上绝对不能再残留 `Mount` / `Language` / `Package` 属性。而对于没写 `Skudef` 的历史老清单，继续按照老旧的 `File@Mount`（`base` / `language` / `optional`）角色规则处理，兼容性保持不变。

解析代码示例：通过 `doc.ManifestRegistration(id)` 获取清单登记节点，然后通过叶子节点的 `ToManifestEntry()` 方法直接转成强类型的 `ManifestEntry`（内置 Files 列表、Dependencies 依赖和 Skudef 计划）。

## 核心元数据模型属性总览

| 模型元素 | 所在 XML 位置 | 属性与功能说明 |
|---|---|---|
| `SchemaVersion` / `ContentRevision` | 根节点属性 | 数据协议大版本号 / 构建注入的 Git 提交修订号 |
| `Application` | 根的子元素 | 应用程序实体：包含 `@ID`、当前版本 `Version`、版本包列表 `Packages`、更新公告 `Posts` |
| `Mod` | 根的子元素 | 模组实体：包含 `@ID`、当前推荐版本 `CurrentVersion`、图标 `Icon`（引用图片 ID）、多语言显示名 `DisplayName`、外观样式 `Style`、版本列表 `Packages`、公告 `Posts` |
| `Package` | 实体子元素 | 具体版本包：包含版本号 `@Version`、发布日期 `ReleaseDate`、更新日志 `Changelogs`、对应的独立清单 `Manifest` |
| `Post` | 实体子元素 | 公告新闻：包含发布时间 `@DateTime`、多语言标题 `Titles` 与正文内容 `Contents` |
| `Image` / `Markdown` / `Manifest` | 资源登记节点 | 包含资源完整 ID `@ID`（带前缀）、相对路径 `@Source`；图片支持 `@Url` 外部链接；清单在主表里表现为占位 stub |

关于外观样式 `Style`（包括 Logo 尺寸、按钮颜色 Controls、背景图 Background）以及公共样式继承（`Base` / `InheritFrom`）的更多细节，可以直接查阅 `Metadata/MetadataSchema.xsd` 与 AGENTS.md。
在源数据中写短名字即可，展平时会自动补全为 `{路径前缀}:{localId}`；顶层实体 ID 全局唯一，一旦撞名打包直接报错拦截。

## 构建与发布命令

```bash
npm run build           # 核心编译打包（完成展平、自动填充变量并执行校验）→ 输出到 ./Output
npm run build:release   # 核心打包 + 图片压缩转为 WebP 格式（正式发版用）
npm run deploy          # 编译打包并直接发布部署到 Cloudflare Pages 线上环境
```

- **核心构建流程**（纯托管 C# 代码执行）：校验源数据 XSD → 将包含 Include 的树展平合并 → 自动替换宏变量 → 复制被引用的图片与文档资源 → 全面业务语义校验；最终输出的 `Output/` 目录里干净利落，只包含合并后的 `metadata.xml`、被引用的资源文件，以及 Cloudflare 的 `_redirects` 路由跳转规则；
- **叶子独立清单处理**：`Manifest` 登记节点的源文件，与大总表入口一样会经过展平处理（移除多余 Include、替换完整 ID、解析宏变量）后再输出发布，客户端直接调 `MetadataBuilder.Load` 就能无缝读取；图片和 Markdown 则直接原样拷贝；
- **图片转换模块（Imaging）**：只负责在发版阶段把大图片转成轻量的 WebP 格式并回传 MD5 哈希，它不打进面向客户端的 NuGet 主包里；只有在带 `--webp` 参数时才会对图片触发转换；
- **XSD 模式规范**：`Metadata/MetadataSchema.xsd` 负责约束作者编写的源数据；`Metadata/MetadataPublishSchema.xsd` 负责约束最终对外发布的产物；
- **内置宏变量**：支持 `${TIMESTAMP}`、`${ENV:NAME}`、`${MD5:}` 等自动替换。

## 自动化测试规范

- **统一使用 MSTest** 测试框架（坚决禁止混入 xunit）；运行命令：`dotnet test Metadata.sln`；
- 测试用例严密覆盖：树的展平合并、XSD 强校验硬失败拦截、ID 与资源定位、防止实体命名冲突、正式发布文件结构、消费端 Catalog 目录解析、LINQ 查询与导航 API、以及挂载计划生成（同时覆盖新版 Skudef 和历史老 Mount 规则）。

## 仓库目录结构

```text
Metadata/                         原始 XML 数据文件以及 XSD 校验模式
Ra3.BattleNet.Metadata/           核心解析库（纯托管 C# 代码）以及命令行构建工具
Ra3.BattleNet.Metadata.Imaging/   仅在正式发布时运行的 WebP 图片压缩转换工具
Ra3.BattleNet.Metadata.Tests/     基于 MSTest 的全套单元测试
AGENTS.md                         专门针对开发者、自动化 Agent 和数据作者的详细编写指南
```
