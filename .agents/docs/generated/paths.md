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
| `Ra3.BattleNet.Metadata/Building/ImagePostProcessor.cs` | 80 |
| `Ra3.BattleNet.Metadata/Building/ImagingInvoker.cs` | 129 |
| `Ra3.BattleNet.Metadata/Building/MarkdownValidator.cs` | 88 |
| `Ra3.BattleNet.Metadata/Building/MetadataBuilder.cs` | 543 |
| `Ra3.BattleNet.Metadata/Building/MetadataInheritance.cs` | 304 |
| `Ra3.BattleNet.Metadata/Building/SchemaValidator.cs` | 132 |
| `Ra3.BattleNet.Metadata/Building/VariableResolver.cs` | 81 |
| `Ra3.BattleNet.Metadata/Cache/HttpMetadataTransfer.cs` | 87 |
| `Ra3.BattleNet.Metadata/Cache/ImageResult.cs` | 24 |
| `Ra3.BattleNet.Metadata/Cache/IMetadataTransfer.cs` | 41 |
| `Ra3.BattleNet.Metadata/Cache/LeafResult.cs` | 34 |
| `Ra3.BattleNet.Metadata/Cache/MetadataCacheJsonContext.cs` | 16 |
| `Ra3.BattleNet.Metadata/Cache/MetadataClient.cs` | 1679 |
| `Ra3.BattleNet.Metadata/Cache/MetadataDiskCache.cs` | 837 |
| `Ra3.BattleNet.Metadata/Cache/MetadataFreshness.cs` | 18 |
| `Ra3.BattleNet.Metadata/Cache/RootSnapshotResult.cs` | 23 |
| `Ra3.BattleNet.Metadata/Exporting/MetadataExporter.cs` | 378 |
| `Ra3.BattleNet.Metadata/Exporting/MetadataExportOptions.cs` | 18 |
| `Ra3.BattleNet.Metadata/Exporting/MetadataSourceExporter.cs` | 310 |
| `Ra3.BattleNet.Metadata/Exporting/MetadataXml.cs` | 34 |
| `Ra3.BattleNet.Metadata/LanguageTag.cs` | 29 |
| `Ra3.BattleNet.Metadata/ManifestMountPlanner.cs` | 156 |
| `Ra3.BattleNet.Metadata/ManifestRawFingerprint.cs` | 33 |
| `Ra3.BattleNet.Metadata/Metadata.cs` | 108 |
| `Ra3.BattleNet.Metadata/MetadataColor.cs` | 43 |
| `Ra3.BattleNet.Metadata/MetadataQueryExtensions.cs` | 339 |
| `Ra3.BattleNet.Metadata/MetadataQueryModels.cs` | 199 |
| `Ra3.BattleNet.Metadata/MetadataResourceUri.cs` | 31 |
| `Ra3.BattleNet.Metadata/MetadataSchema.cs` | 21 |
| `Ra3.BattleNet.Metadata/ModCompatibility.cs` | 61 |
| `Ra3.BattleNet.Metadata/Program.cs` | 95 |
| `Ra3.BattleNet.Metadata/Reading/HttpXmlSource.cs` | 208 |
| `Ra3.BattleNet.Metadata/Reading/IMetadataXmlSource.cs` | 35 |
| `Ra3.BattleNet.Metadata/Reading/MetadataDocumentInfo.cs` | 52 |
| `Ra3.BattleNet.Metadata/Reading/MetadataId.cs` | 34 |
| `Ra3.BattleNet.Metadata/Reading/MetadataParser.cs` | 75 |
| `Ra3.BattleNet.Metadata/Reading/MetadataReader.cs` | 296 |
| `Ra3.BattleNet.Metadata/Reading/MetadataSourceManifest.cs` | 32 |
| `Ra3.BattleNet.Metadata/Reading/MetadataSourceOptions.cs` | 27 |
| `Ra3.BattleNet.Metadata/Reading/MetadataValidator.cs` | 114 |
| `Ra3.BattleNet.Metadata/Reading/PhysicalFileXmlSource.cs` | 76 |

## 测试源码

| 文件 | 说明 |
|---|---|
| `Ra3.BattleNet.Metadata.Tests/Building/BuildTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/ImagingTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/InheritanceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/ManifestParseTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/ManifestRawFingerprintTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/ManifestValidationTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/PublishSurfaceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Building/SchemaTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Cache/CacheJsonContractTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Cache/MetadataClientImageTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Cache/MetadataClientTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Cache/MetadataClientTransferTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Cache/MetadataCompositionCacheTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Exporting/MetadataExportTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Exporting/SourceFormatApiTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/LanguageTagTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/ManifestMountPlannerTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/ModCompatibilityTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/PostHeadImageTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/PostTypeTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/QueryApiTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/StyleResourceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/StyleTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Queries/UpdaterEndpointTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/HttpSourceTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/IdScopeTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/MetadataReaderTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/MetadataTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/PathSecurityTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/RegistrationValidationTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/SourceLoadTests.cs` | MSTest 测试 |
| `Ra3.BattleNet.Metadata.Tests/Reading/ValidationTests.cs` | MSTest 测试 |

## 原始数据树 `Metadata/`

| 目录 | 内容 |
|---|---|
| `Metadata/Templates/` | 一级目录 |
| `Metadata/apps/` | 一级目录 |
| `Metadata/mods/` | 一级目录 |

Mod 实体目录（16 个）：`armor-rush`、`arona-eva`、`corona`、`easternloong`、`eisenreich`、`keva`、`moeva`、`moui`、`neuro-eva`、`neuro-eva-ar`、`olga-eva`、`ra3-censorship-patch`、`remix`、`rie`、`tnw`、`uimod`

Application 实体目录（3 个）：`content`、`ra3battlenet`、`webview2`
