# 版本清单与 Skudef

版本清单描述“这个版本要装哪些文件”。文件表不塞进大总表，客户端按 `Manifest` 登记节点的 `Source` 单独拉取。

---

## 一、清单写在哪，两种都合法

1. **图省事**：直接写在 Mod 主 XML 顶层，`<Manifest ID="…"><File …/></Manifest>`。构建器生成的占位引用会指向这个模块文件本身，文件里因此带了实体定义，体积略大。适合示例和本地测试。
2. **推荐做法**：用 `manifests/manifests.xml` 通过 `Include` 引入每个版本的独立文件 `manifests/<版本号>.xml`。独立文件里只放 `Manifest`、`Skudef`、`File`，引用精确指向它。日冕 Mod 就是这么做的。

独立清单里的示例哈希通常由 Updater 维护，本仓库示例可以填占位值。

```xml
<Manifest ID="manifest-3258" HashAlgorithm="CRC32C">
  <Skudef GameVersion="1.12">
    <AddConfig LocalFile="CustomConfig.txt" Optional="true" />
    <AddBig File="Cor_ENG_3.250.big" Language="en" />
    <AddBig File="corona_3.258.lyi" />
  </Skudef>
  <File Hash="95DC8BF4">
    <FileName>Cor_ENG_3.250.big</FileName>
    <RelativePath>Cor_ENG_3.250.big</RelativePath>
  </File>
</Manifest>
```

## 二、Manifest 与 File 的字段

| 节点或属性 | 必填 | 含义 |
|---|---|---|
| `Manifest@HashAlgorithm` | 否 | `CRC32C`（缺省）、`MD5`、`SHA256` 或 `None`；`None` 表示不做内容哈希与大小校验 |
| `File@Hash` | 否 | 声明的解压后单文件哈希，WebView2 对应 ZIP 而非安装目录；算法由 `HashAlgorithm` 决定，`None` 时省略或为空，其它算法必填 |
| `File@RawHash` | 否 | 下载物原始字节的哈希，例如 `.zip.zst` 的 zstd 字节；省略不改变旧包行为 |
| `File@RawHashAlgorithm` | 否 | 显式值只允许 `SHA256` / `CRC32C`；不写时继承 Manifest 算法；不能脱离 `RawHash` 单独声明 |
| `File@Size` | 否 | 网络压缩包大小，正整数；`None` 算法下客户端不比对 |
| `File@DownloadName` | 否 | 服务器上的压缩包名；不写则与 `FileName` 相同 |
| `File@Compression` | 否 | 压缩算法：`zstd`（解压成单个文件）或 `zip`（解压成目录） |
| `File/FileName` | 是 | 安装后的文件名，也是 `AddBig` 引用的名字 |
| `File/RelativePath` | 是 | 安装位置，标准相对路径，全清单唯一 |
| `File/KindOf` | 是 | 文件类别字符串，库只透传，不做语义判断 |
| `File/Sources/Source` | 否 | 下载地址：`@Type` 为 `HTTP` 或 `BT`；HTTP 要完整绝对地址，BT 要以 `.torrent` 结尾 |
| `Dependencies/Dll` | 否 | 依赖的第三方 DLL：`@Name`、`@Hash` 必填，`@Version`、`@KindOf` 可选 |
| `Skudef@GameVersion` | 否 | 形如 `1.12`，缺省即 `1.12` |
| `Skudef@FileName` | 否 | 启动脚本文件名，如 `AR_3.370.skudef`；不写则由客户端按 `<modId>_<version>.skudef` 生成 |

另有一类尚未实现的声明：`Manifest/SubManifest`（`RelativePath` + `ID`）在 XSD 里合法，但解析库和构建器都不读取它，客户端也不解析。要新增依赖关系时先讨论，不要指望它自动生效。

`Compression="zstd"` 或 `Compression="zip"` 时必须同时写 `DownloadName`，且不能与 `FileName` 相同；`DownloadName` 只能是纯文件名，不能带路径分隔符。

`HashAlgorithm="None"` 表示客户端跳过安装内容的大小与哈希比对，`File@Hash` 必须省略或为空字符串。库保留 `zip` / `None` 通用能力：`zip` 的产物是目录，不能计算清单文件哈希，因此 `Compression="zip"` 只能搭配 `None`。WebView2 不走此路径：它使用 `SHA256` + `zstd`，`RawHash` 校验下载的 `WebView2Runtime.zip.zst`，`Hash` 校验解压后的 `WebView2Runtime.zip`（706254803 字节），随后客户端才解包 ZIP；ZIP 内容无包裹目录。

`RawHash` 必须是符合有效算法长度的十六进制：SHA256 为 64 位、CRC32C 为 8 位；缺省继承 MD5 时为 32 位，不能继承 `None`。查询模型 `RawHashAlgorithm` 返回有效大写算法，不改写原 XML。添加这两个可选属性不提高 `SchemaVersion`，现有非 WebView2 包无需补填。

## 三、Skudef 怎么写

`Skudef` 是启动脚本的**唯一来源**：子元素顺序就是生成的行顺序。

```xml
<Skudef GameVersion="1.12">
  <AddConfig LocalFile="CustomConfig.txt" Optional="true" />
  <AddBig File="Cor_ENG_3.250.big" Language="en" />
  <AddBig File="corona_3.258.lyi" />
</Skudef>
```

- `AddBig@File` 填 `FileName`，全清单每个 `FileName` 必须被恰好引用一次，不能多也不能漏。
- `AddConfig@LocalFile` 填玩家自己放在 Mod 目录下的纯文件名；找不到文件时，`Optional="true"` 放过，缺省报错。
- 加载条件二选一：`@Language` 只在玩家语言匹配时挂载；`@Package` 只在玩家勾选该可选功能包时挂载；都不写就是无条件挂载。
- `@Language`、`@Package` 只能含字母、数字、下划线、连字符，且不能同时出现。
- **写了 `Skudef` 之后，`File` 上不允许再写 `Mount`、`Language`、`Package`**；所有加载规则集中在 `Skudef` 一处。

没写 `Skudef` 的历史清单继续按旧的 `File@Mount`（`base` / `language` / `optional`）角色处理，行为不变。

## 四、构建时会查什么

- 哈希长度与 `HashAlgorithm` 匹配，且是十六进制字符串；非 `None` 算法下 `File@Hash` 必填，`None` 下必须省略或为空。
- 下载物 `RawHash` 的长度与十六进制有效；显式 `RawHashAlgorithm` 仅 `SHA256`/`CRC32C`，且必须同时声明 `RawHash`。
- `Compression="zip"` 必须搭配 `HashAlgorithm="None"`；声明压缩时必须写 `DownloadName` 且与 `FileName` 不同。
- `RelativePath` 唯一、是标准相对路径；`DownloadName` 是纯文件名。
- `Dependencies/Dll@Name` 不重复。
- 写了 `Skudef` 时：`GameVersion` 形如 `1.12`、至少一条挂载指令、`FileName` 全清单唯一、`AddBig@File` 指向真实存在的 `FileName` 且只指一次、所有文件都被挂载到、`AddConfig@LocalFile` 是纯文件名、`File` 上无残留挂载属性。
- 引用清单的地方（`Package/Manifest`）必须指向已登记且类型正确的 `Manifest`。

任一条件不满足就硬失败，输出目录被清空，报错会指出文件和节点。
