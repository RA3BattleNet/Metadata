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
    public void WebPPipeline_UpdatesSourceAndHash()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"webp-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(RepoMetadataDir, dst, contentRevision: "webp-test");
            var flat = Path.Combine(dst, "metadata.xml");
            var before = XDocument.Load(flat);
            var iconBefore = before.Descendants("Image")
                .First(e => e.Attribute("ID")?.Value?.EndsWith(":corona-icon-64px", StringComparison.Ordinal) == true);
            var srcBefore = iconBefore.Attribute("Source")!.Value;
            srcBefore.Should().EndWith(".png");

            var n = WebPPipeline.Process(dst);
            n.Should().BeGreaterThan(0);

            var after = XDocument.Load(flat);
            var icon = after.Descendants("Image")
                .First(e => e.Attribute("ID")?.Value?.EndsWith(":corona-icon-64px", StringComparison.Ordinal) == true);
            icon.Attribute("Source")!.Value.Replace('\\', '/').Should().EndWith(".webp");
            var hash = icon.Attribute("Hash")?.Value;
            hash.Should().NotBeNullOrWhiteSpace();
            hash.Should().HaveLength(32);

            var webpPath = Path.Combine(dst, icon.Attribute("Source")!.Value.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(webpPath).Should().BeTrue();
            WebPPipeline.ComputeMd5(webpPath).Should().Be(hash);
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void ImagingInvoker_FindsProjectInRepo()
    {
        ImagingInvoker.FindImagingProject().Should().NotBeNullOrEmpty();
        File.Exists(ImagingInvoker.FindImagingProject()!).Should().BeTrue();
    }
}
