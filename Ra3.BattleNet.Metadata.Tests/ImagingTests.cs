using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class ImagingTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    [TestMethod]
    public void Build_WithConvertImages_UpdatesXmlViaImagingCli()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"build-webp-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(RepoMetadataDir, dst, contentRevision: "webp", convertImages: true);
            var flat = Path.Combine(dst, "metadata.xml");
            var doc = XDocument.Load(flat);
            var iconId = doc.Descendants("Mod").Single(e => e.Attribute("ID")!.Value == "Corona").Element("Icon")!.Value;
            var icon = doc.Root!.Elements("Image").Single(e => e.Attribute("ID")!.Value == iconId);
            icon.Attribute("Source")!.Value.Replace('\\', '/').Should().EndWith(".webp");
            icon.Attribute("Hash").Should().BeNull("图片不写 Hash");

            var webpPath = Path.Combine(dst, icon.Attribute("Source")!.Value.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(webpPath).Should().BeTrue();

            // 发布面只留被引用资源：转换成功后原图删除
            Directory.GetFiles(dst, "*.png", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }
}
