# Agent Note: 已登记清单与图片的缓存读取节奏

Status: implemented

## Problem

普通展示如果在每次读取时发 HTTP，或在命中图片缓存后再做后台条件校验，页面轮询就会放大请求。图片虚拟地址只带登记 ID 和 revision；进程重启后若 revision 回到相同起点，或同一 ID 更换 Source 后仍得到相同 revision，网页会复用旧图。

## Decision

`MetadataClient` 仍是唯一传输出口。只覆盖 XML `Image` 登记和包叶子。Markdown 正文里未登记的内嵌图片不重写、不缓存。

- `GetCachedLeafAsync(snapshot, version, leafSourceUri, ct = default)` 返回现有 `LeafResult`。生产模式只读内存或有效磁盘叶子，不发 HTTP，不等待在途网络请求，也不把结果标成 `Fresh`。缺失或无法解析返回 `Unavailable`。关闭磁盘缓存时，file:// 每次读取当前源文件并投影为 `Stale`，不落盘；http(s) 仍不主动请求。
- `GetLeafAsync` / `RefreshLeafAsync` 的显式语义不变。
- 启用磁盘缓存时，`GetImageAsync` 命中完整正文就返回，不发 HTTP，也不做后台校验。冷缓存与 `RefreshImageAsync` 共享同一 URI flight。冷失败后十分钟内普通读取不再请求；显式刷新忽略该抑制。缓存写不进去时返回错误并进入同样的抑制，不把未落盘的下载假装成成功。已有旧图时刷新失败保留旧字节，并在 `Error` 写明本次失败。
- `RefreshImageAsync` 发条件请求。首次从不可用变为可展示，或正文被不同字节替换，触发一次 `ImageUpdated`。304 或字节未变不通知。调用方取消只取消等待，`Dispose` 才取消共享传输。通知发生在正文提交之后，回调里读取同一张图只命中缓存。
- `ImageRevision` 使用旁挂摘要，不在列清单时重读图片正文。它是规范化 URI 与摘要的稳定 32 位混合。Source 或摘要变化后这个值会变，从而减少网页复用旧地址。碰撞仍然可能，不能当成绝无复用的证明。不可解析时返回 0。
- 关闭磁盘缓存时，图片每次读取当前来源，不做十分钟抑制，也不发条件请求。

磁盘布局、校验器和最后有效正文不变。图片刷新不参与根 `Fresh` 授权。

## Alternatives considered

- 命中缓存后继续后台校验：实现已有，但普通读取会控制网络。改为显式周期刷新。
- 用进程内递增计数做 revision：重启后回到相同起点，同 ID 换 Source 也可能撞车。改为 URI 加摘要的稳定混合，并接受 32 位碰撞。
- 每次计算 revision 都重读正文并哈希：列表构建会重复图片 IO。改为复用已记录的旁挂摘要；正文完整性仍由实际读取检查。
- 缓存写失败仍返回 Fresh 正文：下一次普通读取看不到磁盘缓存，会再次 GET。改为明示错误并抑制普通重试。

## Consequences

- 展示路径不能把 `GetCachedLeafAsync` 或热图片读取当成 Fresh 授权。
- 叶子就绪不代表图片已经可展示。冷图失败后的首次成功会发 `ImageUpdated`，不能只靠叶子完成信号清掉失败图。
- 旁挂摘要与正文不一致时，revision 仍沿用旁挂值，直到显式刷新或冷读取替换缓存。这是为了避免列表构建重哈希。
- 32 位 revision 碰撞时，虚拟地址可能相同。宿主不能把它当成内容唯一键。
