using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class MetadataTests
{
    private string _testDataPath = null!;

    [TestInitialize]
    public void Init()
    {
        _testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    }

    [TestMethod]
    public void LoadFromFile_ValidXml_Success()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        metadata.Should().NotBeNull();
        metadata.Name.Should().Be("Metadata");
        metadata.Children.Should().HaveCountGreaterThan(0);
    }

    [TestMethod]
    public void LoadFromFile_InvalidRoot_ThrowsException()
    {
        var filePath = Path.Combine(_testDataPath, "invalid-root.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<System.Xml.XmlException>();
    }

    [TestMethod]
    public void LoadFromFile_MissingFile_ThrowsFileNotFoundException()
    {
        var filePath = Path.Combine(_testDataPath, "nonexistent.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<FileNotFoundException>();
    }

    [TestMethod]
    public void Get_MissingVariable_ReturnsDefault()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        metadata.Get("NonExistent", "default").Should().Be("default");
    }

    [TestMethod]
    public void Find_ValidPath_ReturnsMetadata()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var tags = metadata.Find("Tags");
        tags.Should().NotBeNull();
        tags!.Name.Should().Be("Tags");
    }

    [TestMethod]
    public void GetAllElements_ByName_ReturnsMatchingElements()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var applications = metadata.GetAllElements("Application");
        applications.Should().HaveCount(1);
        applications[0].Get("ID").Should().Be("TestApp");
    }

    [TestMethod]
    public void GetElementPath_ReturnsFullPath()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var app = metadata.Find("Application");
        var path = app?.GetElementPath();
        path.Should().Contain("Metadata");
        path.Should().Contain("Application");
    }

    [TestMethod]
    public void LoadFromFile_SourceTreeWithInclude_Throws()
    {
        // Load 只接受展平产物；源树（带 Include）必须走 Build 后再解析
        var filePath = Path.Combine(_testDataPath, "source-with-include.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*只接受展平*");
    }

    [TestMethod]
    public void BuildThenLoad_RepoSample_AppAndModResolve()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));
        var dst = Path.Combine(Path.GetTempPath(), $"load-repo-{Guid.NewGuid():N}");

        try
        {
            // 本地测试链路：Build(源仓) → Load(展平缓存)，与生产 URL 同一套解析
            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "test-rev");
            var metadata = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));

            var corona = metadata.Mods().Single(m => m.Id == "Corona");
            corona.Version.Should().Be("3.229");
            corona.Packages.Should().NotBeEmpty();
            corona.Icon.Should().Contain(":");

            var app = metadata.Catalog().Application("RA3BattleNet");
            app.Should().NotBeNull();
            app!.Version.Should().Be("1.5.2.0");
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }
}
