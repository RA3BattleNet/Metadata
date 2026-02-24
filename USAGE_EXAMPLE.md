# Metadata 库使用指南

## 项目简介

Ra3.BattleNet.Metadata 是一个 XML 元数据处理和解析库，用于管理客户端元数据、模组信息、版本包和更新日志等。

## 快速开始

### 1. 引用库

```csharp
using Ra3.BattleNet.Metadata;
```

### 2. 加载元数据

```csharp
var metadata = Metadata.LoadFromFile("./Metadata/metadata.xml");
```

## 常见使用场景


### 场景0：像你说的那样直接用 `metadata.Mods()` / `mod.Version`

```csharp
var metadata = Metadata.LoadFromFile("./Metadata/metadata.xml");

// 方式A：直接扩展方法
foreach (var mod in metadata.Mods())
{
    Console.WriteLine($"{mod.Id} -> {mod.Version}");
}

var corona = metadata.Mods().FirstOrDefault(m => m.Id == "Corona");
Console.WriteLine(corona?.Version); // 3.229

// 方式B：Catalog 入口（更像“数据仓库”）
var catalog = metadata.Catalog();
var app = catalog.Application("RA3BattleNet");
Console.WriteLine(app?.Version); // 1.5.2.0
```

### 场景1：访问 Corona 模组的 Changelog

```csharp
// 方法1：直接通过 ID 查找 Markdown 元素（推荐）
var changelogMarkdown = metadata.GetElementById("changelog-zh-1.5.2.0");
if (changelogMarkdown != null)
{
    var source = changelogMarkdown.Get("Source");  // zh-1.5.2.0.md
    var hash = changelogMarkdown.Get("Hash");      // MD5 哈希值
    Console.WriteLine($"文件: {source}, 哈希: {hash}");
}

// 方法2：通过 Corona 模组遍历所有版本的 Changelog
var corona = metadata.GetElementById("Corona");
if (corona != null)
{
    var currentVersion = corona.Get("CurrentVersion");
    var packages = corona.Find("Packages");

    foreach (var package in packages.Children.Where(c => c.Name == "Package"))
    {
        var version = package.Get("Version");
        var changelogs = package.Find("Changelogs");

        foreach (var changelog in changelogs.Children.Where(c => c.Name == "Changelog"))
        {
            var language = changelog.Get("Language");
            // Changelog 的 ID 存储在子元素的名称中
            var changelogId = changelog.Children.FirstOrDefault()?.Name;

            // 通过 ID 查找对应的 Markdown 文件
            var md = metadata.GetElementById(changelogId);
            if (md != null)
            {
                var mdSource = md.Get("Source");
                Console.WriteLine($"{version} - {language}: {mdSource}");
            }
        }
    }
}
```

### 场景2：获取所有 Changelog

```csharp
var allMarkdowns = metadata.GetAllElements("Markdown");
var changelogMarkdowns = allMarkdowns.Where(m =>
    m.Get("ID")?.StartsWith("changelog-") == true
).ToList();

foreach (var md in changelogMarkdowns)
{
    Console.WriteLine($"{md.Get("ID")}: {md.Get("Source")}");
}
```

### 场景3：查看 Include 引用树

```csharp
var tree = metadata.GetIncludeTree();
Console.WriteLine(tree);

// 输出示例：
// Metadata
//   Tags
//   Includes
//     Metadata
//       corona [private]
//         Corona
```

## 核心 API 参考

### 查找方法

#### GetElementById(string id)
按 ID 查找元素，支持 public/private 访问控制。

```csharp
var element = metadata.GetElementById("Corona");
```

#### Find(string path)
按路径查找元素，使用 `:` 分隔。

```csharp
var element = metadata.Find("Includes:Metadata:Corona");
```

#### GetAllElements(string name)
获取所有指定名称的元素。

```csharp
var allPackages = metadata.GetAllElements("Package");
```

### 属性访问

#### Get(string key, string? defaultValue = null)
获取元素的属性值。

```csharp
var version = element.Get("Version");
var versionWithDefault = element.Get("Version", "1.0.0");
```

#### Variables
获取所有属性的只读字典。

```csharp
foreach (var attr in element.Variables)
{
    Console.WriteLine($"{attr.Key}: {attr.Value}");
}
```

### 树结构导航

#### Children
获取所有子元素。

```csharp
foreach (var child in element.Children)
{
    Console.WriteLine(child.Name);
}
```

#### Parent
获取父元素。

```csharp
var parent = element.Parent;
```

#### IncludeType
获取 Include 类型（"public" 或 "private"）。

```csharp
if (element.IncludeType == "private")
{
    Console.WriteLine("这是私有 Include");
}
```

### 工具方法

#### GetElementPath()
获取元素的完整路径。

```csharp
var path = element.GetElementPath();
// 输出: Metadata -> Includes -> Metadata -> Corona
```

#### GetIncludeTree(int indent = 0)
获取 Include 引用树的可视化表示。

```csharp
var tree = metadata.GetIncludeTree();
Console.WriteLine(tree);
```

## 元数据结构说明

### XML 结构示例

```xml
<Metadata>
    <Tags>
        <Commit>${ENV:CF_PAGES_COMMIT_SHA}</Commit>
    </Tags>
    <Includes>
        <Include Source="mods/mods.xml" Type="public" />
    </Includes>
</Metadata>
```

### Include 类型

- **Type="public"**: Include 的元素对父节点可见
- **Type="private"**: Include 的元素只在子树中可见（默认）

### 变量替换

支持以下变量语法：

- `${ENV:变量名}` - 系统环境变量
- `${MD5::}` - 当前文件的 MD5 哈希
- `${MD5:文件名}` - 指定文件的 MD5 哈希
- `${META:路径}` - 元数据引用
- `${this:变量}` - 当前模块内引用

## 元数据结构层次

```
Metadata (根)
└── Includes
    ├── Metadata (apps)
    │   └── RA3BattleNet (ID="RA3BattleNet")
    │       ├── Packages
    │       │   └── Package (Version="1.5.2.0")
    │       │       └── Changelogs
    │       │           └── Changelog (Language="zh-CN")
    │       └── Posts
    └── Metadata (mods)
        └── Corona (ID="Corona")
            ├── Packages
            │   └── Package (Version="3.229")
            │       └── Changelogs
            │           └── Changelog (Language="zh-CN")
            └── Posts
```

### 关键点

1. **Markdown 元素**有 `ID` 属性，可以直接通过 `GetElementById()` 查找
2. **Changelog 元素**的 ID 存储在其**子元素的名称**中，不是属性
3. **private Include** 的元素只在子树中可见
4. **public Include** 的元素对父节点也可见

## 构建和运行

### 编译项目

```bash
dotnet build Ra3.BattleNet.Metadata/Ra3.BattleNet.Metadata.csproj
```

### 运行主程序

```bash
dotnet run --project Ra3.BattleNet.Metadata --src="./Metadata" --dst="./Output"
```

主程序会：
1. 加载 metadata.xml
2. 替换环境变量和计算 MD5 哈希
3. 复制所有文件到输出目录
4. 验证处理后的元数据
5. 显示 Include 引用树

### 运行测试

```bash
dotnet test Ra3.BattleNet.Metadata.Tests/Ra3.BattleNet.Metadata.Tests.csproj
```

## 特性

### ✅ 已实现

- XML 元数据解析和验证
- 循环引用检测
- Include 引用关系树（支持 public/private 访问控制）
- 变量替换系统（ENV、MD5、META、this）
- 资源验证和 MD5 哈希计算
- 丰富的查询 API
- 单元测试覆盖

### 🔄 计划中

- XSD Schema 验证
- 详细的错误位置信息（行号、XPath）
- 增量编译支持

## 示例输出

运行主程序后的输出：

```
工作目录: F:\RA3BattleNet\Metadata
元数据源目录: ./Metadata
输出目录: ./Output

加载元数据: ./Metadata\metadata.xml
✓ 成功加载元数据（2 个子节点）
替换环境变量和计算哈希...
✓ 变量替换完成
复制文件到输出目录...
  已复制 15 个文件
✓ 文件复制完成
验证处理后的元数据...
✓ 验证成功

=== 元数据信息 ===
根节点: Metadata
子节点数: 2

=== Include 引用树 ===
Metadata
  Tags
  Includes
    Metadata
      corona [private]
        Corona
      RA3BattleNet
        ...

处理完成！
```
