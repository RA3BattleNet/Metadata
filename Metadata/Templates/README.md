# Templates 脚手架

本目录**不**被 `metadata.xml` Include，仅供复制新建。

## 新建 Mod

1. 复制 `Mod.xml` 到 `mods/<name>/<name>.xml`
2. 改 `Mod/@ID`、资源路径与版本
3. 在 `mods/mods.xml` 增加 `<Include Source="<name>/<name>.xml" Type="public" />`
4. 准备 `images/`、`manifests/` 等实际文件

## 新建 Application

1. 复制 `Application.xml` 到 `apps/<name>/<name>.xml`
2. 改 `Application/@ID` 与 Packages
3. 在 `apps/apps.xml` 增加 Include

## 使用 Base 继承（可选）

见仓库 README「源树继承（Base / InheritFrom）」。  
公共样式可放在独立 XML 的 `<Base ID="..." Kind="Mod">`，Mod 写 `InheritFrom` 只填差异。
