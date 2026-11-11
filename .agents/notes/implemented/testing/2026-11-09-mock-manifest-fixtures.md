# Agent Note: 清单行为用假清单测

Status: implemented

## Problem

发布面和展平测试直接断言仓库里的真实清单路径与版本号。桌面端从 `1.9.9.22` 换成 `2.0.0.0` 之后，`PublishSurfaceTests` 和 `StageATests` 仍在找旧文件，CI 因此失败。清单版本会随发布变化，测试不该跟着改。

## Decision

清单的限定 ID、叶子发布、变量替换和 Skudef 保序，全部用临时目录里的假清单验证。假清单走与正式数据相同的 `Include` 链，版本号固定为测试自己的 `9.9.9` / `1.0`，不读取 `Metadata/apps` 或 `Metadata/mods` 里的现行版本。

仓库样本测试只保留与具体清单无关的断言：发布物包含登记节点指向的资源，查询结果与登记 `Source` 一致。

## Alternatives considered

- **把写死的版本号改成 2.0.0.0**：能让这次 CI 通过，但下一次发布还是会断。
- **从仓库样本动态读当前清单再断言**：不写死版本，却仍依赖线上数据的目录和字段，样本一改测试就漂。

## Consequences

发布换版本时不用再改单元测试。假清单必须自己满足 XSD 和构建校验，缺字段会在测试里失败，而不是在真实数据里被放过。

## Verification

`dotnet test Metadata.sln --filter "FullyQualifiedName~StageATests|FullyQualifiedName~PublishSurfaceTests|FullyQualifiedName~ManifestParseTests|FullyQualifiedName~QueryApiTests"`
