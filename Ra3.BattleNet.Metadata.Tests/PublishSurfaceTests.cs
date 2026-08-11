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
            File.Exists(Path.Combine(dst, "mods", "corona", "changelogs", "zh-3229.md")).Should().BeTrue();
            File.Exists(Path.Combine(dst, "mods", "corona", "manifests", "3.229.xml")).Should().BeTrue();
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
}
