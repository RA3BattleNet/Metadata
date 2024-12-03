# 红警3战网元数据文件

此仓库存储的是战网客户端需要的客户端、插件、模组、地图的描述信息。

【其他说明待编写】

## 开发注记

### {} 内联变量规定：
ENV: 系统环境变量（CF Pages）

MD5: 对文件进行md5 ，MD5:this 指的是计算当前标签的content的文件路径的md5

META: 绝对路径引用，冒号是此处规定的成员运算符

this指当前标签的content

其他：相对路径引用（当前module或者depot下）

**举例：**

{META:CoronaLauncher:LauncherVersion}代表 

{ENV:WINVER} 代表获取系统的winver变量值

{Version} （client.xml中可以找到相同例子），代表同一个Module，也就是Client下的Defines的Version变量值，即1.5.0.0
