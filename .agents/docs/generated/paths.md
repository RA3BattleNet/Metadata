# 仓库路径地图

> 自动生成，勿手改。重新生成：`node scripts/verify-docs.mjs --write`。行数随代码变化，路径以本表为准。

## 项目与解决方案

| 路径 | 说明 |
|---|---|
| `Metadata.sln` | 项目 / 解决方案 |
| `Ra3.BattleNet.Metadata.Imaging/Ra3.BattleNet.Metadata.Imaging.csproj` | 项目 / 解决方案 |
| `Ra3.BattleNet.Metadata.Tests/Ra3.BattleNet.Metadata.Tests.csproj` | 项目 / 解决方案 |
| `Ra3.BattleNet.Metadata/Ra3.BattleNet.Metadata.csproj` | 项目 / 解决方案 |

## XSD 模式

| 路径 | 说明 |
|---|---|
| `Metadata/MetadataPublishSchema.xsd` | XML 模式 |
| `Metadata/MetadataSchema.xsd` | XML 模式 |

## 核心解析库源码

| 文件 | 行数 |
|---|---|
| `Ra3.BattleNet.Metadata/Cache/HttpMetadataTransfer.cs` | 84 |
| `Ra3.BattleNet.Metadata/Cache/ImageResult.cs` | 24 |
| `Ra3.BattleNet.Metadata/Cache/IMetadataTransfer.cs` | 41 |
| `Ra3.BattleNet.Metadata/Cache/LeafResult.cs` | 34 |
| `Ra3.BattleNet.Metadata/Cache/MetadataCacheJsonContext.cs` | 16 |
| `Ra3.BattleNet.Metadata/Cache/MetadataClient.cs` | 1697 |
| `Ra3.BattleNet.Metadata/Cache/MetadataDiskCache.cs` | 734 |
| `Ra3.BattleNet.Metadata/Cache/MetadataFreshness.cs` | 18 |
| `Ra3.BattleNet.Metadata/Cache/RootSnapshotResult.cs` | 23 |
| `Ra3.BattleNet.Metadata/ImagePostProcessor.cs` | 72 |
| `Ra3.BattleNet.Metadata/ImagingInvoker.cs` | 129 |
| `Ra3.BattleNet.Metadata/LanguageTag.cs` | 29 |
| `Ra3.BattleNet.Metadata/ManifestMountPlanner.cs` | 156 |
| `Ra3.BattleNet.Metadata/MarkdownValidator.cs` | 88 |
| `Ra3.BattleNet.Metadata/Metadata.cs` | 120 |
| `Ra3.BattleNet.Metadata/MetadataBuilder.cs` | 775 |
| `Ra3.BattleNet.Metadata/MetadataColor.cs` | 43 |
| `Ra3.BattleNet.Metadata/MetadataFlattener.cs` | 355 |
| `Ra3.BattleNet.Metadata/MetadataInheritance.cs` | 304 |
| `Ra3.BattleNet.Metadata/MetadataParser.cs` | 60 |
| `Ra3.BattleNet.Metadata/MetadataQueryExtensions.cs` | 336 |
| `Ra3.BattleNet.Metadata/MetadataQueryModels.cs` | 197 |
| `Ra3.BattleNet.Metadata/MetadataResourceUri.cs` | 31 |
| `Ra3.BattleNet.Metadata/MetadataSchema.cs` | 21 |
| `Ra3.BattleNet.Metadata/ModCompatibility.cs` | 61 |
| `Ra3.BattleNet.Metadata/Program.cs` | 94 |
| `Ra3.BattleNet.Metadata/SchemaValidator.cs` | 132 |
| `Ra3.BattleNet.Metadata/VariableResolver.cs` | 81 |

## 测试源码

| 文件 | 说明 |
|---|---|
| `Ra3.BattleNet.Metadata.Tests/CacheJsonContractTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/IdScopeTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/ImagingTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/IncludeTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/InheritanceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/LanguageTagTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/ManifestMountPlannerTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/ManifestParseTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/ManifestValidationTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/MetadataClientImageTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/MetadataClientTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/MetadataClientTransferTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/MetadataTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/ModCompatibilityTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/PostHeadImageTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/PostTypeTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/PublishSurfaceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/QueryApiTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/SchemaTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/StageATests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/StyleResourceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/StyleTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/UpdaterEndpointTests.cs` | MSTest 测试 |

## 原始数据树 `Metadata/`

| 目录 | 内容 |
|---|---|
| `Metadata/Templates/` | 一级目录 |
| `Metadata/apps/` | 一级目录 |
| `Metadata/mods/` | 一级目录 |

Mod 实体目录（15 个）：`armor-rush`、`arona-eva`、`corona`、`easternloong`、`eisenreich`、`keva`、`moeva`、`moui`、`neuro-eva`、`neuro-eva-ar`、`olga-eva`、`ra3-censorship-patch`、`rie`、`tnw`、`uimod`

Application 实体目录（2 个）：`content`、`ra3battlenet`
