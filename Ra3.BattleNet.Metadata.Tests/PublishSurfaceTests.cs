using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 发布面：Output 只含展平 metadata.xml + 被引用资源 + _redirects；
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
            File.Exists(Path.Combine(dst, "mods", "corona", "images", "icon-64px.png")).Should().BeTrue();
            File.Exists(Path.Combine(dst, "mods", "corona", "manifests", "3.258.xml")).Should().BeTrue();
            File.Exists(Path.Combine(dst, "apps", "ra3battlenet", "manifests", "1.5.2.0.xml")).Should().BeTrue();

            // _redirects 自动带出
            File.Exists(Path.Combine(dst, "_redirects")).Should().BeTrue();

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
    public void Build_RepoSample_LeafManifestIsFlattenedAndLoadable()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"publish-leaf-{Guid.NewGuid():N}");

        try
        {
            MetadataBuilder.Build(RepoMetadataDir, dst, schemaVersion: "1.0", contentRevision: "publish-test");

            // 叶子清单不能照抄源文件：客户端解析器拒绝 Includes，也不该收到未替换的变量
            var leafPath = Path.Combine(dst, "mods", "corona", "manifests", "3.258.xml");
            var text = File.ReadAllText(leafPath);
            text.Should().NotContain("<Includes");
            text.Should().NotMatchRegex(@"\$\{[^}]+\}");

            var entry = MetadataBuilder.Load(leafPath).Find("Manifest")!.ToManifestEntry();
            entry.HashAlgorithm.Should().Be("CRC32C");
            entry.Files.Should().NotBeEmpty();
            entry.Files.Should().OnlyContain(f => f.Sources.Count > 0);
            entry.Files.Should().Contain(f => f.DownloadName != null);
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Build_RepoSample_SkudefSurvivesFlattenInDeclaredOrder()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"publish-skudef-{Guid.NewGuid():N}");

        try
        {
            MetadataBuilder.Build(RepoMetadataDir, dst, schemaVersion: "1.0", contentRevision: "publish-test");

            var entry = MetadataBuilder.Load(Path.Combine(dst, "mods", "corona", "manifests", "3.258.xml"))
                .Find("Manifest")!.ToManifestEntry();
            var skudef = entry.Skudef;

            skudef.Should().NotBeNull("展平后叶子清单要保住 Skudef");
            skudef!.GameVersion.Should().Be("1.12");
            skudef.Commands.Select(c => c.Target).Should().ContainInOrder(
                "CustomConfig.txt", "Disabler.big", "HighResShadow.big", "Cor_ENG_3.250.big",
                "coronaBGM_3.228.lyi", "corona_3.258.lyi", "StaticVersion.big");
            skudef.Commands.Should().ContainSingle(c => c.Target == "CustomConfig.txt" && c.Kind == SkudefCommandKind.Config && c.Optional);
            skudef.Commands.Should().Contain(c => c.Kind == SkudefCommandKind.Big && c.Target == "Disabler.big" && c.Package == "disable-sky-and-patches");
            skudef.Commands.Should().Contain(c => c.Kind == SkudefCommandKind.Big && c.Target == "HighResShadow.big" && c.Package == "hd-shadow");
            skudef.Commands.Should().Contain(c => c.Kind == SkudefCommandKind.Big && c.Target == "Cor_ENG_3.250.big" && c.Language == "en");
            skudef.Commands.Should().Contain(c => c.Kind == SkudefCommandKind.Big && c.Target == "StaticVersion.big" && c.Package == "world-builder");

            // 加载条件单源：File 上不能再留 Mount/Language/Package
            entry.Files.Should().OnlyContain(f => f.Mount == "base" && f.Language == null && f.Package == null);
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }
}
