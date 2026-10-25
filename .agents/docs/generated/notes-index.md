# 决策笔记索引

> 自动生成，勿手改。重新生成：`node scripts/verify-docs.mjs --write`。

共 10 篇。按状态排序：提案 → 已实施 → 已否决。

| 日期 | 状态 | 分类 | 标题 | 问题 |
|---|---|---|---|---|
| 2026-10-25 | implemented | process | [文档拆分为人类入口与 Agent 地图](../../notes/implemented/process/2026-10-25-agent-first-docs.md) | README.md 303 行、AGENTS.md 226 行，两个文件都在同时承担四类内容：作者写数据的教程、解析库 API、缓存行为、构建与发布流程。 |
| 2026-10-12 | implemented | architecture | [正文先下载到本地文件再解析与发布](../../notes/implemented/architecture/2026-10-12-metadata-transfer-to-local-file.md) | 库原先自己内联 HttpClient，把响应正文读成字节再写缓存：传输不可替换（宿主无法插手代理、重试或平台网络栈），也没法“先把正文落到本地再解析”。 |
| 2026-10-11 | implemented | feature | [Mod 附属身份与版本兼容声明](../../notes/implemented/feature/2026-10-11-mod-launch-compatibility.md) | Desktop 启动集需要知道哪些内容能够作为附属加载，及是否能够与选定主项或其他附属共同使用。 |
| 2026-10-09 | implemented | architecture | [已登记清单与图片的缓存读取节奏](../../notes/implemented/architecture/2026-10-09-registered-resource-cache-cadence.md) | 普通展示如果在每次读取时发 HTTP，或在命中图片缓存后再做后台条件校验，页面轮询就会放大请求。 |
| 2026-10-09 | implemented | feature | [语言标签统一为 zh 与 en](../../notes/implemented/feature/2026-10-09-unified-language-tags.md) | 仓库里跟语言有关的地方写法不统一：实体显示名、新闻标题／简介／正文、友情链接 Languages、清单 AddBig@Language 各写各的，zh-CN / en-US 与 zh / en 混用。 |
| 2026-10-05 | implemented | feature | [Mod 与 Application 的必填显示名](../../notes/implemented/feature/2026-10-05-required-displayname.md) | Mod 与 Application 是客户端产品列表、应用包下载与增量更新任务里直接展示给用户的实体。 |
| 2026-10-04 | implemented | architecture | [Metadata XML 共享加载与最后有效缓存](../../notes/implemented/architecture/2026-10-04-metadata-xml-runtime.md) | MetadataBuilder.Load 的 HTTP 路径每次下载并解析临时文件，不保存运行时缓存。 |
| 2026-10-04 | implemented | feature | [保持元数据版本 1.0](../../notes/implemented/feature/2026-10-04-metadata-schema-version.md) | Style 字段调整时，默认 SchemaVersion 被提高到 2.0。 |
| 2026-10-04 | implemented | feature | [Mod 页面自定义样式](../../notes/implemented/feature/2026-10-04-mod-style.md) | 修改前，本仓库中的元数据清单（metadata.xml）已经包含 <Style> 节点，但在实际使用与规范约束中存在以下… |
| 2026-10-02 | rejected | architecture | [Metadata 解析库的运行期本地缓存](../../notes/rejected/architecture/2026-10-02-runtime-metadata-cache.md) | 本提案原本想为根清单、包清单、媒体资源和跨进程快照建立统一缓存，并计划接入一个包含租约和回收机制的 MetadataRuntime。 |
