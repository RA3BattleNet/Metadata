using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public sealed class SourceLoadTests
{
    [TestMethod]
    public void Read_PhysicalTree_PreservesFilesAndVariablesAndIncludedManifestBody()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "parts"));
        try
        {
            var entry = Path.Combine(root, "entry.xml");
            const string xml = """
<Metadata><Tags><Commit>${TIMESTAMP}</Commit><Flavor>${ENV:MISSING}</Flavor></Tags><Include Source="parts/release.xml"/><Mod ID="Demo"><Packages><Package Version="1"><Manifest>release</Manifest></Package></Packages></Mod></Metadata>
""";
            File.WriteAllText(entry, xml);
            File.WriteAllText(Path.Combine(root, "parts", "release.xml"), """
<Metadata><Manifest ID="release"><File Path="game.bin" Size="0"/></Manifest><Markdown ID="news" Source="news.md"/></Metadata>
""");
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            var result = MetadataReader.Read(entry);
            result.Root.Find("Tags:Commit")!.Value.Should().Be("${TIMESTAMP}");
            result.Root.Find("Tags:Flavor")!.Value.Should().Be("${ENV:MISSING}");
            result.Root.ManifestRegistration("parts/release/release")!.GetAllElements("File").Should().ContainSingle();
            result.Root.ManifestRegistration("parts/release/release")!.Get("Source").Should().BeNull();
            result.Root.Markdowns().Single().Source.Should().Be("parts/news.md");
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().BeEquivalentTo(before.Keys);
            foreach (var (path, bytes) in before) File.ReadAllBytes(path).Should().Equal(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Read_ShortExternalStubAndLeaf_HaveExactIdentityWithSharedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "leaves"));
        try
        {
            var entry = Path.Combine(root, "entry.xml");
            var leafPath = Path.Combine(root, "leaves", "1.0.xml");
            File.WriteAllText(entry, """
<Metadata><Manifest ID="release" Source="leaves/1.0.xml"/><Mod ID="Demo"><Packages><Package Version="1"><Manifest>release</Manifest></Package></Packages></Mod></Metadata>
""");
            File.WriteAllText(leafPath, "<Metadata><Manifest ID=\"release\"><File Path=\"game.bin\" Size=\"0\"/></Manifest></Metadata>");
            var read = MetadataReader.Read(entry);
            var stub = read.Root.ManifestRegistration("leaves/1.0/release")!;
            stub.Get("Source").Should().Be("leaves/1.0.xml");
            read.Root.Mods().Single().Packages.Single().ManifestId.Should().Be("leaves/1.0/release");
            var leaf = MetadataReader.Read(leafPath, new MetadataSourceOptions { SourceRoot = read.SourceRoot });
            leaf.Root.GetAllElements("Manifest").Single().Get("ID").Should().Be(stub.Get("ID"));
            MetadataReader.Read(entry, new MetadataSourceOptions { InlineManifests = true }).Root
                .ManifestRegistration("leaves/1.0/release")!.GetAllElements("File").Should().ContainSingle();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
