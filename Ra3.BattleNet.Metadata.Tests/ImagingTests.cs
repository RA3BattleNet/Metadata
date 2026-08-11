using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata.Imaging;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class ImagingTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    [TestMethod]
    public void ImageConverter_ReturnsHashOfWebpFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"img-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(RepoMetadataDir, "mods", "corona", "images", "icon-64px.png");
            var webp = Path.Combine(dir, "icon.webp");
            var hash = ImageConverter.ConvertToWebP(png, webp);
            hash.Should().HaveLength(32);
            File.Exists(webp).Should().BeTrue();
            ImageConverter.ComputeMd5(webp).Should().Be(hash);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Build_WithConvertImages_UpdatesXmlViaImagingCli()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"build-webp-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(RepoMetadataDir, dst, contentRevision: "webp", convertImages: true);
            var flat = Path.Combine(dst, "metadata.xml");
            var doc = XDocument.Load(flat);
            var icon = doc.Descendants("Image")
                .First(e => e.Attribute("ID")?.Value?.EndsWith(":corona-icon-64px", StringComparison.Ordinal) == true);
            icon.Attribute("Source")!.Value.Replace('\\', '/').Should().EndWith(".webp");
            var hash = icon.Attribute("Hash")!.Value;
            hash.Should().HaveLength(32);
            var webpPath = Path.Combine(dst, icon.Attribute("Source")!.Value.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(webpPath).Should().BeTrue();
            ImageConverter.ComputeMd5(webpPath).Should().Be(hash);

            // 发布面只留被引用资源：转换成功后原图删除
            Directory.GetFiles(dst, "*.png", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void ImagingInvoker_ConvertToWebP_ReturnsHash()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"invoker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(RepoMetadataDir, "mods", "corona", "images", "icon-64px.png");
            var webp = Path.Combine(dir, "out.webp");
            var hash = ImagingInvoker.ConvertToWebP(png, webp);
            hash.Should().HaveLength(32);
            File.Exists(webp).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
