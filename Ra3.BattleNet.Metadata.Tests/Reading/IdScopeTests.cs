using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public sealed class IdScopeTests
{
    [TestMethod]
    public void PathPrefix_EscapesDefinitionSegmentsAndPreservesVersionDots()
    {
        var root = Path.Combine(Path.GetTempPath(), "source");
        MetadataId.PathPrefix(Path.Combine(root, "space name", "3.259.xml"), root)
            .Should().Be("space%20name/3.259");
        MetadataId.Qualify("space%20name/3.259", "icon%name").Should().Be("space%20name/3.259/icon%25name");
        MetadataId.LocalId("space%20name/3.259/icon%25name").Should().Be("icon%name");
    }


    [TestMethod]
    [DataRow("Mod")]
    [DataRow("Application")]
    public void Read_DuplicateEntityIdsAcrossDocuments_ReportsBothDefinitions(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "entry.xml"), "<Metadata><Include Source=\"a.xml\"/><Include Source=\"b.xml\"/></Metadata>");
            File.WriteAllText(Path.Combine(root, "a.xml"), "<Metadata><Mod ID=\"Same\"/></Metadata>");
            File.WriteAllText(Path.Combine(root, "b.xml"), $"<Metadata><{kind} ID=\"Same\"/></Metadata>");
            var act = () => MetadataReader.Read(Path.Combine(root, "entry.xml"));
            var exception = act.Should().Throw<InvalidOperationException>().Which;
            exception.Message.Should().Contain(Path.Combine(root, "a.xml")).And.Contain(Path.Combine(root, "b.xml"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
