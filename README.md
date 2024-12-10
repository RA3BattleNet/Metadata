# 红警3战网元数据文件

此仓库存储的是战网客户端需要的客户端、数据包、模组、地图的描述信息。

【其他说明待编写】

## 模块说明

``` xml
    <Includes>
        <!-- 子模块 -->
        <Include Source="ra3battlenet/ra3battlenet.xml" Type="public" />
        <Include Source="coronalauncher/coronalauncher.xml" Type="public" />
    </Includes>

    <Includes>
        <!-- 子模块 -->
        <Include Source="changelogs/changelogs.xml" Type="private" />
        <Include Source="manifests/manifests.xml" Type="private" />
        <Include Source="posts/posts.xml" Type="private" />
    </Includes>
```

大部分XML文件都包含Includes字段，本质上Metadata文件可以合并为一个大的XML。但是为了方便编辑和维护进行拆分
Type分为 public 和 private，public指的是可以被展平（暴露给上级引用者，别的文件可以访问），private代表私有引用仅在此文件中可用，别的文件无法访问

### 元数据需求

Metadata 需要存储的数据有：

1. 客户端本身的版本信息和元数据信息，包括：客户端版本号，数据包最新版本号，客户端更新列表，数据包更新列表，多语言翻译文件等
2. Mod下载功能（预计50+Mod）需要包含：Mod列表，每个Mod都需要包含ModID，Mod版本号，Mod介绍（图文混合，可能是Markdown），Mod更新列表（更新列表要支持多个文件，并且要支持对不同文件定义哈希值和下载方式），新闻列表，新闻信息（图文混合）
3. 地图下载功能：预留

于是分为：Application, Markdown, Image, 


存储信息的主要格式为XML，因为数据会引用包含图片素材和HTML/MarkDown素材，所以可能会有文件和XML放在一起。以ID的方法引用，每个松散数据都需要验证哈希（在引用时同时声明MD5，但此MD5需要进行计算）

于是，XML数据需要支持变量，比如 ${MD5:<ID>} 是对指定ID的松散文件进行哈希值计算，并在Build时替换
${MD5::}指的是对当前文件进行哈希计算
需要编写XSD文件，方便对XML进行验证

### XML的 ${} 内联变量规定：
ENV: 系统环境变量（CF Pages）

MD5: 对文件进行md5 ，${MD5::} 指的是计算当前标签文件路径的md5

或者 ${MD5:xxxx.txt} 代表对指定文件进行哈希

META: 绝对路径引用，冒号是此处规定的成员运算符

其他：相对路径引用（当前module或者depot下）

**举例：**

${META:CoronaLauncher:LauncherVersion}代表 

${ENV:WINVER} 代表获取系统的winver变量值

${this:Version} （client.xml中可以找到相同例子），代表同一个Module，也就是Client下的Defines的Version变量值，即1.5.0.0
