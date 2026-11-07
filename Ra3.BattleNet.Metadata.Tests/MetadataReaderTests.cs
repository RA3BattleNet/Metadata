using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public sealed class MetadataReaderTests
{
    [TestMethod]
    public void Read_BaseWithoutInclude_ResolvesInheritanceAndResourceReferences()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Image ID="icon" Source="icon.png"/><Base ID="standard" Kind="Mod"><Icon>icon</Icon></Base><Mod ID="Demo" InheritFrom="standard"/></Metadata>
""");
        var result = Read(source);
        result.Root.Mods().Single().Icon.Should().Be("entry:icon");
        result.Document.Descendants("Base").Should().BeEmpty();
        source.LoadDocument(MemorySource.Root + "entry.xml").Descendants("Image").Single().Attribute("ID")!.Value.Should().Be("icon");
    }

    [TestMethod]
    public void Read_ThreeMatchingAliases_RemainAmbiguous()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Includes><Include Source="a.xml"/><Include Source="b.xml"/><Include Source="c.xml"/></Includes><Mod ID="Demo"><Icon>shared</Icon></Mod></Metadata>
""");
        foreach (var name in new[] { "a", "b", "c" })
            source.Add(name + ".xml", "<Metadata><Image ID=\"shared\" Url=\"https://assets.test/icon.png\"/></Metadata>");
        var act = () => Read(source);
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Read_ChildAndBaseReferencesResolveParentAndSiblingRegistrations()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Include Source="child.xml"/><Include Source="base.xml"/><Include Source="resources.xml"/><Image ID="parentIcon" Url="https://assets.test/parent.png"/><Mod ID="Inherited" InheritFrom="standard"/></Metadata>
""");
        source.Add("child.xml", """
<Metadata><Mod ID="Child"><Icon>parentIcon</Icon><Style><Logo>siblingLogo</Logo></Style></Mod></Metadata>
""");
        source.Add("base.xml", """
<Metadata><Base ID="standard" Kind="Mod"><Icon>parentIcon</Icon><Style><Logo>siblingLogo</Logo></Style></Base></Metadata>
""");
        source.Add("resources.xml", """
<Metadata><Image ID="siblingLogo" Url="https://assets.test/sibling.png"/></Metadata>
""");
        var result = Read(source);
        foreach (var mod in result.Root.Mods())
        {
            mod.Icon.Should().Be("entry:parentIcon");
            mod.Raw.Find("Style:Logo")!.Value.Should().Be("resources:siblingLogo");
        }
        MetadataValidator.FindMissingReferences(result.Root).Should().BeEmpty();
    }

    [TestMethod]
    public void Read_MixedAuthorAndNormalizedPath_PreservesScopeAndAddress()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Include Source="split/flat.xml"/><Image ID="local" Source="local.png"/><Mod ID="Demo"><Icon>icon</Icon></Mod></Metadata>
""");
        source.Add("split/flat.xml", """
<Metadata IdFormat="path" SourceBase="document"><Image ID="original/icons/icon" Source="picture.png"/></Metadata>
""");
        var result = Read(source);
        result.Root.Images().Select(i => i.Id).Should().BeEquivalentTo("original/icons:icon", "entry:local");
        result.Root.Images().Single(i => i.Id == "original/icons:icon").Source.Should().Be("split/picture.png");
        result.Documents[0].Includes.Single().TargetPath.Should().Be(MemorySource.Root + "split/flat.xml");
        result.Documents[1].NormalizedDocument.Descendants("Image").Single().Attribute("Source")!.Value
            .Should().Be(MemorySource.Root + "split/picture.png");
    }

    [TestMethod]
    public void Read_AuthorLocalReferenceWinsOverNormalizedUnscopedRegistration()
    {
        var source = new MemorySource();
        source.Add("entry.xml", "<Metadata><Include Source=\"published.xml\"/><Include Source=\"child.xml\"/></Metadata>");
        source.Add("published.xml", """
<Metadata IdFormat="legacy" SourceBase="document"><Image ID="icon" Url="https://assets.test/published.png"/><Mod ID="Published"><Icon>icon</Icon></Mod></Metadata>
""");
        source.Add("child.xml", """
<Metadata><Image ID="icon" Url="https://assets.test/child.png"/><Mod ID="Child"><Icon>icon</Icon></Mod></Metadata>
""");
        var root = Read(source).Root;
        root.Mods().Single(m => m.Id == "Child").Icon.Should().Be("child:icon");
        root.Mods().Single(m => m.Id == "Published").Icon.Should().Be("icon");
        MetadataValidator.FindMissingReferences(root).Should().BeEmpty();
    }

    [TestMethod]
    public void Read_EntryBelowAllowedRoot_ResourceBaseRemainsEntry()
    {
        var source = new MemorySource();
        source.Add("nested/entry.xml", """
<Metadata><Base ID="unused" Kind="Mod"/><Image ID="icon" Source="icon.png"/></Metadata>
""");
        var result = MetadataReader.Read(MemorySource.Root + "nested/entry.xml", new MetadataSourceOptions
        {
            CustomSource = source,
            SourceRoot = MemorySource.Root
        });
        result.Root.Images().Single().Source.Should().Be("icon.png");
        result.Root.Images().Single().Id.Should().Be("nested/entry:icon");
    }

    [TestMethod]
    public void Read_UnmarkedFlat_PreservesShortIdsAndSource()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Image ID="icon" Source="../assets/icon.png"/><Mod ID="Demo"><Icon>icon</Icon></Mod></Metadata>
""");
        var result = Read(source);
        result.Root.Images().Single().Id.Should().Be("icon");
        result.Root.Images().Single().Source.Should().Be("../assets/icon.png");
    }

    [TestMethod]
    public void Read_UnmarkedHttpFlatPreservesFileResourceSourcesWithoutLoadingThem()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata SchemaVersion="1.0"><Image ID="icon" Source="file:///local/icon.png"/><Markdown ID="post" Source="file:///local/post.md"/><Mod ID="Demo"><Icon>icon</Icon></Mod></Metadata>
""");
        var result = Read(source);
        result.Root.Images().Single().Source.Should().Be("file:///local/icon.png");
        result.Document.Descendants("Markdown").Single().Attribute("Source")!.Value.Should().Be("file:///local/post.md");
        source.Loads.Should().Equal(MemorySource.Root + "entry.xml");
        source.Probes.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_NormalizedHttpDocumentStillRejectsFileResourceSources()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata IdFormat="legacy" SourceBase="document"><Image ID="icon" Source="file:///local/icon.png"/></Metadata>
""");
        var act = () => Read(source);
        act.Should().Throw<InvalidOperationException>();
        source.Loads.Should().Equal(MemorySource.Root + "entry.xml");
        source.Probes.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_CanonicalDuplicatesAcrossDocuments_Fail()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Include Source="a.xml"/><Include Source="b.xml"/></Metadata>
""");
        source.Add("a.xml", "<Metadata IdFormat=\"legacy\"><Image ID=\"scope:icon\"/></Metadata>");
        source.Add("b.xml", "<Metadata IdFormat=\"path\"><Markdown ID=\"scope/ICON\"/></Metadata>");
        var act = () => Read(source);
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Read_HttpPathsDifferingOnlyByCase_AreDifferentDocuments()
    {
        var source = new MemorySource();
        source.Add("entry.xml", "<Metadata><Include Source=\"A.xml\"/><Include Source=\"a.xml\"/></Metadata>");
        source.Add("A.xml", "<Metadata><Mod ID=\"Upper\"/></Metadata>");
        source.Add("a.xml", "<Metadata><Mod ID=\"Lower\"/></Metadata>");
        Read(source).Root.Mods().Select(m => m.Id).Should().BeEquivalentTo("Upper", "Lower");
    }

    [TestMethod]
    public void Read_DiamondGraph_CapturesAndExpandsOnce()
    {
        var source = new MemorySource();
        source.Add("entry.xml", "<Metadata><Include Source=\"a.xml\"/><Include Source=\"shared.xml\"/></Metadata>");
        source.Add("a.xml", "<Metadata><Include Source=\"shared.xml\"/></Metadata>");
        source.Add("shared.xml", "<Metadata><Mod ID=\"Shared\"/></Metadata>");
        var result = Read(source);
        result.Documents.Should().HaveCount(3);
        result.Root.Mods().Should().ContainSingle();
    }

    [TestMethod]
    public void Read_NormalizedManifestInsideAuthorIncludes_RemainsInline()
    {
        var source = new MemorySource();
        source.Add("entry.xml", "<Metadata><Include Source=\"wrapper.xml\"/></Metadata>");
        source.Add("wrapper.xml", "<Metadata><Include Source=\"normalized.xml\"/></Metadata>");
        source.Add("normalized.xml", """
<Metadata IdFormat="legacy" SourceBase="document"><Manifest ID="release:v1"><File Path="game.bin" Size="0"/></Manifest><Mod ID="Demo"><Packages><Package Version="1.0"><Manifest>release:v1</Manifest></Package></Packages></Mod></Metadata>
""");
        var manifest = Read(source).Root.ManifestRegistration("release:v1")!;
        manifest.Get("Source").Should().BeNullOrEmpty();
        manifest.GetAllElements("File").Should().ContainSingle();
    }

    [TestMethod]
    public void Read_ExternalManifestComposition_UsesSameReader()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Manifest ID="release" Source="leaves/wrapper.xml"/><Mod ID="Demo"><Packages><Package Version="1.0"><Manifest>release</Manifest></Package></Packages></Mod></Metadata>
""");
        source.Add("leaves/wrapper.xml", "<Metadata><Include Source=\"actual.xml\"/></Metadata>");
        source.Add("leaves/actual.xml", "<Metadata><Manifest ID=\"release\"><File Path=\"game.bin\" Size=\"0\"/></Manifest></Metadata>");
        var result = MetadataReader.Read(MemorySource.Root + "entry.xml", new MetadataSourceOptions
        {
            CustomSource = source,
            InlineManifests = true
        });
        result.Root.ManifestRegistration("release")!.GetAllElements("File").Should().ContainSingle();
        result.Documents.Should().HaveCount(3);
    }

    [TestMethod]
    public void ReadAndOpen_UrlImageDoesNotResolveInformationalSource()
    {
        var source = new MemorySource();
        source.Add("entry.xml", """
<Metadata><Base ID="unused" Kind="Mod"/><Image ID="icon" Url="https://assets.test/icon.png" Source="../../outside.png"/></Metadata>
""");
        var settings = new MetadataSourceOptions { CustomSource = source };
        var read = MetadataReader.Read(MemorySource.Root + "entry.xml", settings).Root;
        var opened = MetadataBuilder.Open(MemorySource.Root + "entry.xml", settings);
        read.Images().Single().Source.Should().Be("../../outside.png");
        opened.Images().Single().Source.Should().Be("../../outside.png");
        source.Loads.Should().OnlyContain(path => path == MemorySource.Root + "entry.xml");
        source.Probes.Should().BeEmpty();
    }

    [TestMethod]
    public void Read_IncludeTraversalFailsBeforeLoadingOutsideDocument()
    {
        var source = new MemorySource();
        source.Add("entry.xml", "<Metadata><Include Source=\"../outside.xml\"/></Metadata>");
        var act = () => Read(source);
        act.Should().Throw<InvalidOperationException>();
        source.Loads.Should().Equal(MemorySource.Root + "entry.xml");
    }

    [TestMethod]
    public void PathCodec_RoundTripsEscapedSegmentsWithoutChangingVersionDots()
    {
        const string id = "mods/space name/versions:3.259/a%icon";
        var encoded = MetadataId.EncodePath(id);
        encoded.Should().Contain("3.259");
        MetadataId.DecodePath(encoded).Should().Be(id);
        var invalidScope = () => MetadataId.DecodePath("a%2Fb/icon");
        invalidScope.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Read_UnknownDeclaredFormatFails()
    {
        var source = new MemorySource();
        source.Add("entry.xml", "<Metadata IdFormat=\"unknown\"/>");
        var act = () => Read(source);
        act.Should().Throw<InvalidOperationException>();
    }

    private static MetadataReadResult Read(MemorySource source) => MetadataReader.Read(MemorySource.Root + "entry.xml",
        new MetadataSourceOptions { CustomSource = source });

    private sealed class MemorySource : IMetadataXmlSource
    {
        internal const string Root = "https://reader.test/root/";
        private readonly Dictionary<string, XDocument> _documents = new(StringComparer.Ordinal);
        internal List<string> Loads { get; } = [];
        internal List<string> Probes { get; } = [];
        internal void Add(string relative, string xml) => _documents[Root + relative] = XDocument.Parse(xml);
        public bool Exists(string path)
        {
            Probes.Add(path);
            return _documents.ContainsKey(GetFullPath(path));
        }
        public XDocument LoadDocument(string path)
        {
            Loads.Add(path);
            return new XDocument(_documents[path]);
        }
        public string Combine(string basePath, string relativePath) => new Uri(new Uri(basePath.EndsWith('/') ? basePath : basePath + "/"), relativePath).AbsoluteUri;
        public string? GetDirectoryName(string path) => new Uri(new Uri(path), ".").AbsoluteUri;
        public string GetRelativePath(string fromPath, string toPath)
        {
            var prefix = fromPath.EndsWith('/') ? fromPath : fromPath + "/";
            return toPath.StartsWith(prefix, StringComparison.Ordinal) ? toPath[prefix.Length..] : toPath;
        }
        public string GetFullPath(string path) => new Uri(new Uri(Root), path).AbsoluteUri;
        public bool IsPathRooted(string path) => Uri.TryCreate(path, UriKind.Absolute, out _);
    }
}
