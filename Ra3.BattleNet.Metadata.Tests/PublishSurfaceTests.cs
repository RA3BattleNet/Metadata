using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 发布面：Output 只含展平 metadata.xml + 被引用资源 + _redirects + editor.html；
/// 源 XML / XSD / Templates 一律不进发布物。
/// </summary>
[TestClass]
public class PublishSurfaceTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    private static void SeedSchemas(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
        {
            File.Copy(Path.Combine(RepoMetadataDir, name), Path.Combine(targetDir, name), overwrite: true);
        }
    }

    [TestMethod]
    public void Build_RepoSample_OutputHasOnlyFlattenedAndReferencedResources()
    {
        var src = RepoMetadataDir;
        var dst = Path.Combine(Path.GetTempPath(), $"publish-repo-{Guid.NewGuid():N}");

        try
        {
            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "publish-test");

            // 展平数据 + 入口
            File.Exists(Path.Combine(dst, "metadata.xml")).Should().BeTrue();
            File.ReadAllText(Path.Combine(dst, "metadata.xml")).Should().NotContain("<Include");

            // 被引用资源都在
            var loaded = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));
            foreach (var image in loaded.Images().Where(image => image.Source is not null))
            {
                var relative = image.Source!.Replace('/', Path.DirectorySeparatorChar);
                File.ReadAllBytes(Path.Combine(dst, relative))
                    .Should().Equal(File.ReadAllBytes(Path.Combine(src, relative)));
            }
            foreach (var manifest in loaded.GetAllElements("Manifest").Where(m => m.Get("Source") is not null))
            {
                var relative = manifest.Get("Source")!.Replace('/', Path.DirectorySeparatorChar);
                File.Exists(Path.Combine(dst, relative)).Should().BeTrue($"登记清单必须进发布物: {relative}");
                File.Exists(Path.Combine(src, relative)).Should().BeTrue();
            }

            // _redirects 自动带出
            File.Exists(Path.Combine(dst, "_redirects")).Should().BeTrue();

            // editor.html 原样带出
            var editorSource = Path.Combine(src, "editor.html");
            var editorOutput = Path.Combine(dst, "editor.html");
            File.Exists(editorOutput).Should().BeTrue();
            File.ReadAllBytes(editorOutput).Should().Equal(File.ReadAllBytes(editorSource));

            // 发布面不含源树资产
            Directory.GetFiles(dst, "*.xsd", SearchOption.AllDirectories).Should().BeEmpty();
            Directory.Exists(Path.Combine(dst, "Templates")).Should().BeFalse();
            File.Exists(Path.Combine(dst, "mods", "mods.xml")).Should().BeFalse();
            File.Exists(Path.Combine(dst, "mods", "corona", "corona.xml")).Should().BeFalse();
            File.Exists(Path.Combine(dst, "apps", "apps.xml")).Should().BeFalse();
            File.Exists(Path.Combine(dst, "apps", "ra3battlenet", "ra3battlenet.xml")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Build_UrlOnlyImage_CopiesNothingForIt()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"publish-url-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "img.png"), "fake-image-bytes");
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="remote-only" Url="https://example.com/img.png" />
  <Image ID="with-source" Source="img.png" />
</Metadata>
""");

            MetadataBuilder.Build(src, dst, contentRevision: "x");

            // Url 优先：remote-only 不落盘；with-source 落盘
            Directory.GetFiles(dst, "img.png", SearchOption.AllDirectories).Should().HaveCount(1);
            File.ReadAllText(Path.Combine(dst, "metadata.xml"))
                .Should().Contain("remote-only").And.Contain("with-source");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_MockLeafManifest_IsFlattenedAndLoadable()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"publish-mock-leaf-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "mods", "mockmod", "manifests"));
            Directory.CreateDirectory(Path.Combine(src, "apps", "mockruntime", "manifests"));
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <Metadata>
                  <Includes>
                    <Include Source="mods/mockmod/mockmod.xml" />
                    <Include Source="apps/mockruntime/mockruntime.xml" />
                  </Includes>
                </Metadata>
                """);
            File.WriteAllText(Path.Combine(src, "mods", "mockmod", "mockmod.xml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <Metadata>
                  <Includes>
                    <Include Source="manifests/1.0.xml" />
                  </Includes>
                  <Mod ID="MockMod">
                    <CurrentVersion>1.0</CurrentVersion>
                    <DisplayName Language="zh">假模组</DisplayName>
                    <DisplayName Language="en">MockMod</DisplayName>
                    <Packages>
                      <Package Version="1.0">
                        <Manifest>manifest-mock</Manifest>
                      </Package>
                    </Packages>
                    <Posts/>
                  </Mod>
                </Metadata>
                """);
            File.WriteAllText(Path.Combine(src, "mods", "mockmod", "manifests", "1.0.xml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <Metadata>
                  <Tags>
                    <Commit>${TIMESTAMP}</Commit>
                  </Tags>
                  <Includes/>
                  <Manifest ID="manifest-mock" HashAlgorithm="CRC32C">
                    <Skudef GameVersion="1.12">
                      <AddConfig LocalFile="CustomConfig.txt" Optional="true" />
                      <AddBig File="core.big" Package="optional-pack" />
                      <AddBig File="text.big" Language="en" />
                      <AddBig File="base.lyi" />
                    </Skudef>
                    <File Hash="3507CD54" Size="4">
                      <FileName>core.big</FileName>
                      <RelativePath>/</RelativePath>
                      <KindOf>MISC;</KindOf>
                      <Sources>
                        <Source Type="BT" Url="https://example.invalid/core.big.torrent" />
                      </Sources>
                    </File>
                    <File Hash="88BE39D6" Size="4">
                      <FileName>text.big</FileName>
                      <RelativePath>/</RelativePath>
                      <KindOf>MISC;</KindOf>
                      <Sources>
                        <Source Type="BT" Url="https://example.invalid/text.big.torrent" />
                      </Sources>
                    </File>
                    <File Hash="95DC8BF4" Size="8" DownloadName="base.zst" Compression="zstd">
                      <FileName>base.lyi</FileName>
                      <RelativePath>/</RelativePath>
                      <KindOf>MOD;</KindOf>
                      <Sources>
                        <Source Type="BT" Url="https://example.invalid/base.zst.torrent" />
                      </Sources>
                    </File>
                  </Manifest>
                </Metadata>
                """);
            File.WriteAllText(Path.Combine(src, "apps", "mockruntime", "mockruntime.xml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <Metadata>
                  <Includes>
                    <Include Source="manifests/1.0.xml" />
                  </Includes>
                  <Application ID="MockRuntime">
                    <Version>1.0</Version>
                    <DisplayName Language="zh">假运行时</DisplayName>
                    <DisplayName Language="en">MockRuntime</DisplayName>
                    <Packages>
                      <Package Version="1.0">
                        <Manifest>manifest-runtime</Manifest>
                      </Package>
                    </Packages>
                    <Posts/>
                  </Application>
                </Metadata>
                """);
            File.WriteAllText(Path.Combine(src, "apps", "mockruntime", "manifests", "1.0.xml"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <Metadata>
                  <Manifest ID="manifest-runtime" HashAlgorithm="SHA256">
                    <File Hash="31de4e57a19017eee55e6f66ddb9f6c217f944716dee2cd14a37f72dfbbe831b" RawHash="b601d79b38720d174c17b6affed3506f01c5033d20a42d1f90fe5745f1b8d021" RawHashAlgorithm="SHA256" Size="64" DownloadName="runtime.zip.zst" Compression="zstd">
                      <FileName>runtime.zip</FileName>
                      <RelativePath>/</RelativePath>
                      <KindOf>RUNTIME</KindOf>
                      <Sources>
                        <Source Type="BT" Url="https://example.invalid/runtime.zip.zst.torrent" />
                      </Sources>
                    </File>
                  </Manifest>
                </Metadata>
                """);

            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "publish-mock");

            var leafPath = Path.Combine(dst, "mods", "mockmod", "manifests", "1.0.xml");
            var text = File.ReadAllText(leafPath);
            text.Should().NotContain("<Includes");
            text.Should().NotMatchRegex(@"\$\{[^}]+\}");

            var entry = MetadataBuilder.Load(leafPath).Find("Manifest")!.ToManifestEntry();
            entry.HashAlgorithm.Should().Be("CRC32C");
            entry.Files.Should().NotBeEmpty();
            entry.Files.Should().OnlyContain(f => f.Sources.Count > 0);
            entry.Files.Should().Contain(f => f.DownloadName != null);

            var runtime = MetadataBuilder.Load(Path.Combine(dst, "apps", "mockruntime", "manifests", "1.0.xml"))
                .Find("Manifest")!.ToManifestEntry();
            runtime.HashAlgorithm.Should().Be("SHA256");
            var file = runtime.Files.Should().ContainSingle().Subject;
            file.FileName.Should().Be("runtime.zip");
            file.DownloadName.Should().Be("runtime.zip.zst");
            file.Compression.Should().Be("zstd");
            file.Size.Should().Be(64);
            file.Hash.Should().Be("31de4e57a19017eee55e6f66ddb9f6c217f944716dee2cd14a37f72dfbbe831b");
            file.RawHash.Should().Be("b601d79b38720d174c17b6affed3506f01c5033d20a42d1f90fe5745f1b8d021");
            file.RawHashAlgorithm.Should().Be("SHA256");
            file.Sources.Should().ContainSingle().Which.Url.Should()
                .Be("https://example.invalid/runtime.zip.zst.torrent");
            var exported = MetadataXml.Write(file.Raw);
            exported.Should().Contain($"RawHash=\"{file.RawHash}\"").And.Contain("RawHashAlgorithm=\"SHA256\"");

            var skudef = entry.Skudef;
            skudef.Should().NotBeNull("展平后叶子清单要保住 Skudef");
            skudef!.GameVersion.Should().Be("1.12");
            skudef.Commands.Select(c => c.Target).Should().ContainInOrder(
                "CustomConfig.txt", "core.big", "text.big", "base.lyi");
            skudef.Commands.Should().ContainSingle(c => c.Target == "CustomConfig.txt" && c.Kind == SkudefCommandKind.Config && c.Optional);
            skudef.Commands.Should().Contain(c => c.Kind == SkudefCommandKind.Big && c.Target == "core.big" && c.Package == "optional-pack");
            skudef.Commands.Should().Contain(c => c.Kind == SkudefCommandKind.Big && c.Target == "text.big" && c.Language == "en");
            entry.Files.Should().OnlyContain(f => f.Mount == "base" && f.Language == null && f.Package == null);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

}
