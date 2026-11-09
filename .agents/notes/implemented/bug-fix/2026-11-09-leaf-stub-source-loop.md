# Agent Note: 叶子清单不再追自己的存根 Source

Status: implemented

## Problem

`MetadataClient.FetchLeafDocumentAsync` 读叶子清单时打开了 `InlineManifests`，`CaptureGraphAsync` 会把文档里所有带 `ID` + `Source` 的 `Manifest` 当成待内联的正文继续抓取。团队为此写的缓存测试 `Leaf_RejectsStubCycleAndMissingFile_AcceptsEmptyManifest` 用一个“叶子清单本身只有 `<Manifest ID="app-leaf" Source="apps/leaf.xml"/>`”的存根来验证拒绝路径：

- 存根被当成待内联清单，`Source` 按叶子自身地址解析，得到 `apps/apps/leaf.xml`；
- 那个地址返回同一份存根，再解析成 `apps/apps/apps/leaf.xml`……地址无限增长，请求无限打。

Windows 上缓存目录路径很快超过 MAX_PATH，抛 `IOException` 被兜住，测试表现为“快速失败”；Linux 没有路径长度限制，于是在 CI 里跑到超时。CI 只挂在 Ubuntu，本地 Windows 全绿，正是这个原因。

## Decision

- 删掉 `CaptureGraphAsync` 的 `inlineManifests` 参数与 Manifest 追踪分支；它只保留 `Include` 递归。
- 读叶子清单不再打开 `InlineManifests`：叶子本身就是正文目标，里面出现带 `Source` 的 `Manifest` 只可能是存根，交给 `TryAcceptLeaf` 直接报「叶子清单是登记存根」。
- 根清单读取、`MetadataReader` 的 `InlineManifests` 选项与导出流程不变。
- 补回归测试 `Leaf_StubSource_IsRejectedWithoutFollowingItself`：断言只发根清单 + 目标叶子两次请求，状态为 `Unavailable`，错误含「存根」。

## Alternatives considered

- **在追踪处判断目标地址与当前文档相同就停下**：能挡住自引用；但地址每层都会变（`apps/apps/...`），单点比较挡不住增长链。
- **按 Manifest ID 去重**：自引用场景能停；但存根本身就该拒绝，靠去重只是把它变成“第二次遇到才停”，语义仍然别扭。
- **只限制最大抓取深度**：能防死循环；但把错误变成“深度超限”，不如直接按存根拒绝，且仍会发出多余请求。

## Consequences

- 存根叶子现在一次请求就判失败，不再放大成无限请求链。
- 客户端不再把叶子里的 `Manifest@Source` 当正文追踪；发布契约本来也不允许叶子同时带 body 与 Source。
- Linux 与 Windows 行为一致；缓存测试在两条平台上都在秒级完成。

## Verification

- WSL Ubuntu + .NET 10 全量测试：430 通过，10 秒完成（修复前 3 分钟无进展、被 hang 探针中止）。
- Windows 全量测试：430 通过。
- 回归测试确认请求次数为 2，错误为存根拒绝。
