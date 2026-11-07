using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class MetadataExportTests
{
    [TestMethod]
    public void EveryLayoutAndEncoding_RoundTripsAssetsAndCanonicalIdentity()
    {
        var sandbox = CreateFixture();
        try
        {
            var input = MetadataReader.Read(Path.Combine(sandbox, "source", "metadata.xml"));
            var original = input.Document.ToString();
            var captured = input.Documents.Select(document => document.NormalizedDocument.ToString()).ToArray();
            foreach (var layout in Enum.GetValues<MetadataExportLayout>())
            foreach (var format in Enum.GetValues<MetadataExportIdFormat>())
            foreach (var inlineManifests in new[] { false, true })
            {
                var output = Path.Combine(sandbox, $"{layout}-{format}-{inlineManifests}");
                MetadataExporter.Export(input, output, new MetadataExportOptions
                {
                    Layout = layout,
                    IdFormat = format,
                    Inline = path => path == "mods/demo.xml",
                    InlineManifests = inlineManifests
                });
                var result = MetadataReader.Read(Path.Combine(output, "metadata.xml"), new MetadataSourceOptions { InlineManifests = true });
                result.Root.Images().Select(image => image.Id).Should().BeEquivalentTo(input.Root.Images().Select(image => image.Id));
                result.Root.Mods().Single().Raw.Find("Icon")!.Value.Should().Be("mods/demo:icon");
                result.Root.Mods().Single().Id.Should().Be("Demo");
                result.Root.ManifestRegistration("leaf:m")!.Find("File:FileName")!.Value.Should().Be("demo.big");
                var image = result.Root.Images().Single(entry => entry.Source is not null);
                File.ReadAllText(MetadataResourceUri.Resolve(Path.Combine(output, "metadata.xml"), image.Source!).LocalPath)
                    .Should().Be("image-bytes");
                var markdown = result.Root.Markdowns().Single();
                File.ReadAllText(MetadataResourceUri.Resolve(Path.Combine(output, "metadata.xml"), markdown.Source!).LocalPath)
                    .Should().Be("# news");
                var remote = result.Root.Images().Single(entry => entry.Url is not null);
                remote.Url.Should().Be("https://example.test/icon.png");
                remote.Source.Should().BeNull();
                if (layout == MetadataExportLayout.Flat)
                    XDocument.Load(Path.Combine(output, "metadata.xml")).Descendants("Include").Should().BeEmpty();
                if (layout == MetadataExportLayout.Tree)
                    File.Exists(Path.Combine(output, "mods", "demo.xml")).Should().BeTrue();
                input.Document.ToString().Should().Be(original);
                input.Documents.Select(document => document.NormalizedDocument.ToString()).Should().Equal(captured);
            }
            File.ReadAllText(Path.Combine(sandbox, "source", "mods", "icon.png")).Should().Be("image-bytes");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void Mixed_SelectsTargetEdgesAndWritesDocumentRelativeSources()
    {
        var sandbox = CreateFixture();
        try
        {
            var input = MetadataReader.Read(Path.Combine(sandbox, "source", "metadata.xml"));
            var output = Path.Combine(sandbox, "mixed");
            MetadataExporter.Export(input, output, new MetadataExportOptions
            {
                Layout = MetadataExportLayout.Mixed,
                Inline = path => path == "mods/demo.xml",
                InlineManifests = true
            });
            var root = XDocument.Load(Path.Combine(output, "metadata.xml"));
            root.Root!.Elements("Mod").Should().ContainSingle();
            root.Descendants("Include").Select(node => (string?)node.Attribute("Source")).Should().Equal("leaf.xml");
            File.Exists(Path.Combine(output, "mods", "demo.xml")).Should().BeFalse();
            root.Descendants("Image").Single(node => node.Attribute("Source") is not null)
                .Attribute("Source")!.Value.Should().Be("mods/icon.png");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void ExportAgain_KeepsCanonicalIdsWithoutPhysicalPathPrefixes()
    {
        var sandbox = CreateFixture();
        try
        {
            var input = MetadataReader.Read(Path.Combine(sandbox, "source", "metadata.xml"));
            var first = Path.Combine(sandbox, "first");
            var second = Path.Combine(sandbox, "second");
            MetadataExporter.Export(input, first, new MetadataExportOptions { Layout = MetadataExportLayout.Tree, IdFormat = MetadataExportIdFormat.Path });
            MetadataExporter.Export(MetadataReader.Read(Path.Combine(first, "metadata.xml")), second,
                new MetadataExportOptions { Layout = MetadataExportLayout.Flat, IdFormat = MetadataExportIdFormat.Path, InlineManifests = true });
            var result = MetadataReader.Read(Path.Combine(second, "metadata.xml"));
            result.Root.Images().Select(image => image.Id).Should().BeEquivalentTo(input.Root.Images().Select(image => image.Id));
            result.Root.ManifestRegistration("leaf:m")!.Find("File:FileName")!.Value.Should().Be("demo.big");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void Failure_ClearsOnlyOutputAndLeavesInputUntouched()
    {
        var sandbox = CreateFixture();
        try
        {
            var source = Path.Combine(sandbox, "source");
            var input = MetadataReader.Read(Path.Combine(source, "metadata.xml"));
            var output = Path.Combine(sandbox, "output");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "old.txt"), "old");
            File.Delete(Path.Combine(source, "mods", "icon.png"));
            var export = () => MetadataExporter.Export(input, output);
            export.Should().Throw<FileNotFoundException>();
            Directory.Exists(output).Should().BeFalse();
            File.Exists(Path.Combine(source, "metadata.xml")).Should().BeTrue();
            var overlap = () => MetadataExporter.Export(input, source);
            overlap.Should().Throw<InvalidOperationException>();
            File.Exists(Path.Combine(source, "metadata.xml")).Should().BeTrue();
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void Planning_RejectsTraversalAndOutputCollisionsBeforeWriting()
    {
        var sandbox = CreateFixture();
        try
        {
            var source = Path.Combine(sandbox, "source");
            var input = MetadataReader.Read(Path.Combine(source, "metadata.xml"));
            var output = Path.Combine(sandbox, "output");
            var image = input.Documents.Single(document => document.RelativePath == "mods/demo.xml")
                .NormalizedDocument.Descendants("Image").Single(node => node.Attribute("Source") is not null);
            image.SetAttributeValue("Source", Path.Combine(sandbox, "outside.png"));
            var traversal = () => MetadataExporter.Export(input, output);
            traversal.Should().Throw<InvalidOperationException>();
            Directory.Exists(output).Should().BeFalse();
            image.SetAttributeValue("Source", Path.Combine(source, "metadata.xml"));
            var collision = () => MetadataExporter.Export(input, output);
            collision.Should().Throw<InvalidOperationException>();
            Directory.Exists(output).Should().BeFalse();
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void SharedExternalManifestSource_PreservesEveryRegistration()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "metadata-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(sandbox, "source");
            Write(source, "metadata.xml", "<Metadata><Manifest ID=\"one\" Source=\"both.xml\"/><Manifest ID=\"two\" Source=\"both.xml\"/></Metadata>");
            Write(source, "both.xml", "<Metadata><Manifest ID=\"one\" HashAlgorithm=\"None\"><File><FileName>one.big</FileName></File></Manifest><Manifest ID=\"two\" HashAlgorithm=\"None\"><File><FileName>two.big</FileName></File></Manifest></Metadata>");
            var output = Path.Combine(sandbox, "output");
            MetadataExporter.Export(MetadataReader.Read(Path.Combine(source, "metadata.xml")), output);
            var leaf = XDocument.Load(Path.Combine(output, "both.xml"));
            leaf.Root!.Elements("Manifest").Select(element => (string?)element.Attribute("ID")).Should().Equal("one", "two");
            var result = MetadataReader.Read(Path.Combine(output, "metadata.xml"), new MetadataSourceOptions { InlineManifests = true });
            result.Root.ManifestRegistration("one")!.Find("File:FileName")!.Value.Should().Be("one.big");
            result.Root.ManifestRegistration("two")!.Find("File:FileName")!.Value.Should().Be("two.big");
        }
        finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void Tree_ExternalManifestInsideBusinessDocument_DoesNotReferenceItself()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "metadata-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(sandbox, "source");
            Write(source, "metadata.xml", "<Metadata><Include Source=\"parts/mod.xml\"/></Metadata>");
            Write(source, "parts/mod.xml", """
                <Metadata>
                  <Manifest ID="payload" HashAlgorithm="None"><File><FileName>demo.big</FileName></File></Manifest>
                  <Mod ID="Demo"><Packages><Package Version="1"><Manifest>payload</Manifest></Package></Packages></Mod>
                </Metadata>
                """);
            var input = MetadataReader.Read(Path.Combine(source, "metadata.xml"));
            foreach (var layout in Enum.GetValues<MetadataExportLayout>())
            {
                var output = Path.Combine(sandbox, layout.ToString());
                MetadataExporter.Export(input, output, new MetadataExportOptions
                {
                    Layout = layout,
                    Inline = _ => false,
                    IdFormat = MetadataExportIdFormat.Path
                });
                var result = MetadataReader.Read(Path.Combine(output, "metadata.xml"),
                    new MetadataSourceOptions { InlineManifests = true });
                result.Root.ManifestRegistration("parts/mod:payload")!.Find("File:FileName")!.Value.Should().Be("demo.big");
                if (layout != MetadataExportLayout.Flat)
                {
                    var business = XDocument.Load(Path.Combine(output, "parts", "mod.xml"));
                    business.Root!.Elements("Mod").Should().ContainSingle();
                    business.Root.Elements("Manifest").Single().Attribute("Source")!.Value.Should().NotBe("mod.xml");
                }
            }
        }
        finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
    }

    [TestMethod]
    public void EmptyFullManifest_AllLayoutsPreserveHashAlgorithmAndRoundTrip()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "metadata-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(sandbox, "source");
            Write(source, "metadata.xml", "<Metadata><Include Source=\"leaf.xml\"/></Metadata>");
            Write(source, "leaf.xml", "<Metadata><Manifest ID=\"empty\" HashAlgorithm=\"None\"/></Metadata>");
            var input = MetadataReader.Read(Path.Combine(source, "metadata.xml"));
            foreach (var layout in Enum.GetValues<MetadataExportLayout>())
            foreach (var inline in new[] { false, true })
            {
                var output = Path.Combine(sandbox, $"{layout}-{inline}");
                MetadataExporter.Export(input, output, new MetadataExportOptions
                {
                    Layout = layout,
                    Inline = _ => true,
                    InlineManifests = inline,
                    IdFormat = MetadataExportIdFormat.Path
                });
                var result = MetadataReader.Read(Path.Combine(output, "metadata.xml"),
                    new MetadataSourceOptions { InlineManifests = true });
                var manifest = result.Root.ManifestRegistration("leaf:empty")!;
                manifest.Get("HashAlgorithm").Should().Be("None");
                manifest.Get("Source").Should().BeNull();
                manifest.Children.Should().BeEmpty();
                if (!inline)
                {
                    var leaf = XDocument.Load(Path.Combine(output, "leaf.xml"));
                    leaf.Root!.Element("Manifest")!.Attribute("HashAlgorithm")!.Value.Should().Be("None");
                }
            }
        }
        finally { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); }
    }

    private static string CreateFixture()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "metadata-export-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(sandbox, "source");
        Write(source, "metadata.xml", "<Metadata><Includes><Include Source=\"mods/demo.xml\"/><Include Source=\"leaf.xml\"/></Includes></Metadata>");
        Write(source, "mods/demo.xml", """
            <Metadata>
              <Image ID="icon" Source="icon.png"/><Image ID="remote" Url="https://example.test/icon.png"/>
              <Markdown ID="news" Source="news.md"/>
              <Mod ID="Demo"><Icon>icon</Icon><DisplayName Language="en">Demo</DisplayName><DisplayName Language="zh">Demo</DisplayName>
                <Posts><Post><Contents><Content Language="en">news</Content></Contents></Post></Posts>
                <Packages><Package Version="1"><Manifest>leaf:m</Manifest></Package></Packages>
              </Mod>
            </Metadata>
            """);
        Write(source, "leaf.xml", "<Metadata><Manifest ID=\"m\" HashAlgorithm=\"None\"><File><FileName>demo.big</FileName><RelativePath>/</RelativePath></File></Manifest></Metadata>");
        Write(source, "mods/icon.png", "image-bytes");
        Write(source, "mods/news.md", "# news");
        return sandbox;
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
