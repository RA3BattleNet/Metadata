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
    public void LoadFromFile_CircularReference_ThrowsException()
    {
        var filePath = Path.Combine(_testDataPath, "circular-a.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*循环引用*");
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
    public void GetElementById_ExistingId_ReturnsElement()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var app = metadata.GetElementById("TestApp");
        app.Should().NotBeNull();
        app!.Get("ID").Should().Be("TestApp");
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
    public void ReplaceVariablesInFile_RecursivelyProcessesIncludeFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"metadata-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var rootPath = Path.Combine(tempDir, "metadata.xml");
            var includePath = Path.Combine(tempDir, "included.xml");
            File.WriteAllText(rootPath, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Defines>
    <Commit>${ENV:TEST_COMMIT}</Commit>
  </Defines>
  <Include Source="included.xml" Type="public" />
</Metadata>
""");
            File.WriteAllText(includePath, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Defines>
    <Value>${ENV:TEST_COMMIT}</Value>
  </Defines>
</Metadata>
""");
            Environment.SetEnvironmentVariable("TEST_COMMIT", "abc123", EnvironmentVariableTarget.Process);
            var metadata = Metadata.LoadFromFile(rootPath);
            metadata.ReplaceVariablesInFile(rootPath);
            File.ReadAllText(rootPath).Should().Contain("abc123").And.NotContain("${ENV:TEST_COMMIT}");
            File.ReadAllText(includePath).Should().Contain("abc123").And.NotContain("${ENV:TEST_COMMIT}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void GetIncludeTree_ReturnsTreeStructure()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        metadata.GetIncludeTree().Should().Contain("Metadata");
    }

    [TestMethod]
    public void ToNodeTree_ShouldKeepLeafValue()
    {
        var filePath = Path.Combine(_testDataPath, "valid-metadata.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var root = metadata.ToNodeTree();
        var app = root.Children.Single(c => c.Name == "Application");
        var appName = app.Children.Single(c => c.Name == "Name");
        appName.Value.Should().Be("Test Application");
    }

    [TestMethod]
    public void RepoSample_LoadSource_HasAppAndMod()
    {
        var filePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata", "metadata.xml"));
        var metadata = Metadata.LoadFromFile(filePath);
        metadata.GetBusinessEntities().Should().Contain(e => e.EntityType == "Application" && e.Id == "RA3BattleNet");
        metadata.GetBusinessEntities().Should().Contain(e => e.EntityType == "Mod" && e.Id == "Corona");
    }

    [TestMethod]
    public void Mods_ShouldExposeVersionAndPackages()
    {
        var filePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata", "metadata.xml"));
        var metadata = Metadata.LoadFromFile(filePath);
        var corona = metadata.Mods().Single(m => m.Id == "Corona");
        corona.Version.Should().Be("3.229");
        corona.Packages.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Catalog_ShouldProvideConvenientLookup()
    {
        var filePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata", "metadata.xml"));
        var metadata = Metadata.LoadFromFile(filePath);
        var app = metadata.Catalog().Application("RA3BattleNet");
        app.Should().NotBeNull();
        app!.Version.Should().Be("1.5.2.0");
    }
}
