# Templates 模板脚手架

这个目录下的文件**不会**被主入口 `metadata.xml` 直接引入，纯粹是为了方便大家新建 Mod 或应用时复制当模板用的。

## 如何新建一个 Mod

1. 复制本目录下的 `Mod.xml` 到 `mods/<你的Mod英文名>/<你的Mod英文名>.xml`；
2. 修改文件里的 `Mod/@ID`、资源文件相对路径、版本号，以及 `zh-CN` / `en-US` 两条必填 `DisplayName`（可再补其它语言）；
3. 打开 `mods/mods.xml`，在里面加上一行：`<Include Source="<你的Mod英文名>/<你的Mod英文名>.xml" />`；
4. 把实际要用到的图片放到 `images/`、安装清单放到 `manifests/` 目录下。

## 如何新建一个 Application

1. 复制本目录下的 `Application.xml` 到 `apps/<你的应用英文名>/<你的应用英文名>.xml`；
2. 修改文件里的 `Application/@ID`、`zh-CN` / `en-US` 两条必填 `DisplayName`（可再补其它语言），以及包含的 `Packages` 版本包列表；
3. 打开 `apps/apps.xml`，在里面加上对应的 `<Include Source="..." />` 引入行。

## 公共样式继承（可选的高级功能）

如果多个 Mod 想共用同一套界面按钮样式或背景，可以使用继承机制：
把公共样式写在单独 XML 文件的 `<Base ID="..." Kind="Mod">` 标签里，具体 Mod 的 XML 里直接写 `InheritFrom="<公共Base的ID>"`，里面只写自己的个性化差异部分即可。
详细介绍可以查阅仓库根目录下的 [README.md](../../README.md) 和 [AGENTS.md](../../AGENTS.md)。
