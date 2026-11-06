# Agent Note: WebView2 zstd 包采用双层内容指纹

Status: implemented

## Problem

旧 WebView2 清单以 `Compression="zip"`、`HashAlgorithm="None"` 描述解包目录，不能验证下载物和待安装 ZIP 的内容。完整运行时现在通过 zstd 压缩的 ZIP 分发；下载缓存复用与 ZIP 解包之前需要分别确认两个文件的身份。

## Decision

`File` 增加可选 `RawHash`、`RawHashAlgorithm`，`SchemaVersion` 保持 `1.0`。`RawHash` 描述下载原始字节，现有 `Hash` 仍描述声明解压后的单文件，不能改作目录摘要。显式原始算法仅接受 SHA256、CRC32C；省略时继承 Manifest 算法（CRC32C、MD5、SHA256），不能继承 None。原始指纹必须符合有效算法的十六进制长度，孤立算法、空指纹、非法长度和非十六进制均拒绝。没有原始指纹的现有包不需要补填。

构建器与强类型查询复用 `ManifestRawFingerprint` 校验；新属性本身触发构建语义校验，不能靠缺少 Manifest 算法或 Sources 绕过。缓存读取调用同一查询投影，因此非法叶子不会变成有效缓存结果。强类型 `RawHashAlgorithm` 返回有效大写算法，XML 保留作者声明，不补写缺省属性。

WebView2 叶子采用 SHA256 + zstd：下载名 `WebView2Runtime.zip.zst`，大小 233263294；解压名 `WebView2Runtime.zip`，ZIP 大小 706254803。RawHash 为 `b601d79b38720d174c17b6affed3506f01c5033d20a42d1f90fe5745f1b8d021`，Hash 为 `31de4e57a19017eee55e6f66ddb9f6c217f944716dee2cd14a37f72dfbbe831b`。BT 地址为 `https://file.cor-games.com/srv/redist/WebView2Runtime.zip.zst.torrent`。ZIP 根直接包含 `msedgewebview2.exe` 与 `EBWebView/x86`、`x64`，没有包裹目录；安装解包属于 Desktop，不属于本库。

源 schema 同时负责叶子 File 表；发布根 schema 只有 Manifest 存根，没有 File 定义，保持不变。构建叶子、缓存正文和 XML 导出原样保留属性，不引入另一套序列化列表。通用 zip/None 能力仍保留，本次只迁移 WebView2 数据，不扩展编辑器 UI。

## Alternatives considered

- **继续 zip/None**：数据与安装路径都最简单，但放弃完整包的下载与 ZIP 指纹校验，不满足运行时迁移目标。
- **把 Hash 改成下载物摘要**：不新增字段，但会改变所有已有清单的语义，并失去 ZIP 身份验证；因此采用两个可选属性而非重定义旧字段。
- **删除 Metadata 的 zip/None 通用能力**：客户端可干净切换，但库是通用数据契约，删除能力扩大了本次迁移范围，故只让 WebView2 不再使用旧路径。

## Consequences

作者可以独立选择下载物和解压产物的算法；查询模型新增两个末尾可选参数，原有构造调用不需要补参。显式 MD5 被拒绝，但从 Manifest 继承 MD5 仍遵守缺省算法契约。非 WebView2 包继续按原声明读取。

历史笔记按 WebView、RawHash、Compression、免校验检索：现有 source-format-host-api 仅提及宿主边界，属于无关决定；没有需要取代或归档的运行时指纹笔记。

## Verification

新增 MSTest 覆盖合法缺省和显式指纹、无指纹兼容、非法算法/内容/长度/孤立算法、None 继承拒绝、构建旧格式绕过防护、真实 WebView2 发布叶子读取、XML 属性导出、磁盘缓存重开及非法缓存叶子拒绝。

主代理验证 `dotnet test Metadata.sln` 全部 349 项通过；发布构建 `dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output` 通过，生成的 WebView2 叶子用于 Desktop 真实完整包安装冒烟。`node scripts/verify-docs.mjs --write` 与只读检查通过。本地包保持在 Downloads，不进入版本库。
