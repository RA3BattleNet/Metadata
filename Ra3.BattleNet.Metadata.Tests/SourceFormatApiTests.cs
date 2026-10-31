using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 面向宿主的源格式 API：导出单 Mod 作者声明、解析本地声明里的版本清单、解析独立 Manifest 节点。
/// </summary>
[TestClass]
public class SourceFormatApiTests
{
    private const string FlattenedRoot = """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata SchemaVersion="1.0">
  <Image ID="corona:icon" Source="mods/corona/images/icon.png" />
  <Image ID="corona:link" Url="https://example.com/link.png" />
  <Markdown ID="corona:news" Source="mods/corona/posts/news.md" />
  <Manifest ID="corona:manifest-3258" Source="mods/corona/manifests/3.258.xml" />
  <Mod ID="Corona">
    <CurrentVersion>3.259</CurrentVersion>
    <Icon>corona:icon</Icon>
    <DisplayName Language="zh">日冕</DisplayName>
    <DisplayName Language="en">Corona</DisplayName>
    <Style>
      <Logo>corona:icon</Logo>
      <Background><Image>corona:icon</Image><Image>corona:icon</Image></Background>
    </Style>
    <Posts>
      <Post>
        <HeadImage>corona:icon</HeadImage>
        <Contents><Content Language="zh">corona:news</Content><Content Language="en">corona:news</Content></Contents>
      </Post>
    </Posts>
    <Links>
      <Link><Icon>corona:link</Icon></Link>
    </Links>
    <Packages>
      <Package Version="3.258"><Manifest>corona:manifest-3258</Manifest></Package>
      <Package Version="3.259"><Manifest>corona:manifest-3259</Manifest></Package>
    </Packages>
  </Mod>
  <Mod ID="Other">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">别的</DisplayName>
    <DisplayName Language="en">Other</DisplayName>
  </Mod>
</Metadata>
""";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"source-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void DeleteTemp(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [TestMethod]
    public void ExportWhole_KeepsEveryVersionAndShortIds_WithAbsoluteSources()
    {
        var root = MetadataParser.Load(XDocument.Parse(FlattenedRoot));

        var xml = XElement.Parse(MetadataSourceExporter.ExportWhole(
            root, "corona", "https://metadata.example.com/metadata.xml"));

        xml.Name.LocalName.Should().Be("Metadata");
        var mod = xml.Elements("Mod").Single();
        mod.Attribute("ID")!.Value.Should().Be("Corona");
        mod.Element("CurrentVersion")!.Value.Should().Be("3.259");
        mod.Elements("Packages").Elements("Package").Select(p => p.Attribute("Version")!.Value)
            .Should().Equal("3.258", "3.259");
        mod.Elements("Packages").Elements("Package").Select(p => p.Element("Manifest")!.Value)
            .Should().Equal("manifest-3.258", "manifest-3.259");

        // 只抄这个 Mod 直接引用的登记，不夹带 Other。
        xml.Elements("Mod").Should().ContainSingle();
        var image = xml.Elements("Image").Single(i => i.Attribute("Source") is not null);
        image.Attribute("Source")!.Value
            .Should().Be("https://metadata.example.com/mods/corona/images/icon.png");

        // Url 型图片只保留外链，不猜 Source。
        var link = xml.Elements("Image").Single(i => i.Attribute("Url") is not null);
        link.Attribute("Source").Should().BeNull();

        var markdown = xml.Elements("Markdown").Single();
        markdown.Attribute("Source")!.Value
            .Should().Be("https://metadata.example.com/mods/corona/posts/news.md");

        // 引用已改写成生成的短 ID。
        var iconId = image.Attribute("ID")!.Value;
        mod.Element("Icon")!.Value.Should().Be(iconId);
        mod.Element("Style")!.Element("Logo")!.Value.Should().Be(iconId);
        mod.Element("Posts")!.Element("Post")!.Element("HeadImage")!.Value.Should().Be(iconId);
        mod.Element("Posts")!.Element("Post")!.Element("Contents")!.Elements("Content")
            .Select(c => c.Value).Should().AllBe(markdown.Attribute("ID")!.Value);
    }

    [TestMethod]
    public void ExportWhole_WithoutBaseUrl_KeepsRelativeSources()
    {
        var root = MetadataParser.Load(XDocument.Parse(FlattenedRoot));

        var xml = XElement.Parse(MetadataSourceExporter.ExportWhole(root, "Corona"));

        xml.Elements("Image").Single(i => i.Attribute("Source") is not null)
            .Attribute("Source")!.Value.Should().Be("mods/corona/images/icon.png");
        xml.Elements("Markdown").Single()
            .Attribute("Source")!.Value.Should().Be("mods/corona/posts/news.md");
    }

    [TestMethod]
    public void ExportVersion_KeepsSelectedPackageOnly_AndMakesItCurrent()
    {
        var root = MetadataParser.Load(XDocument.Parse(FlattenedRoot));

        var mod = XElement.Parse(MetadataSourceExporter.ExportVersion(root, "Corona", "3.258"))
            .Elements("Mod").Single();

        mod.Element("CurrentVersion")!.Value.Should().Be("3.258");
        var package = mod.Elements("Packages").Elements("Package").Should().ContainSingle().Subject;
        package.Attribute("Version")!.Value.Should().Be("3.258");
        package.Element("Manifest")!.Value.Should().Be("manifest-3.258");
    }

    [TestMethod]
    public void Export_UnknownModOrVersion_Throws()
    {
        var root = MetadataParser.Load(XDocument.Parse(FlattenedRoot));

        var missingMod = () => MetadataSourceExporter.ExportWhole(root, "Ghost");
        missingMod.Should().Throw<InvalidOperationException>().WithMessage("*Ghost*");

        var missingVersion = () => MetadataSourceExporter.ExportVersion(root, "Corona", "9.9");
        missingVersion.Should().Throw<InvalidOperationException>().WithMessage("*9.9*");
    }

    [TestMethod]
    public void Export_DoesNotMutateTheLoadedTree()
    {
        var root = MetadataParser.Load(XDocument.Parse(FlattenedRoot));

        MetadataSourceExporter.ExportWhole(root, "Corona", "https://metadata.example.com/metadata.xml");

        root.Mods().Single(m => m.Id == "Corona").Raw.Find("Icon")!.Value.Should().Be("corona:icon");
        root.Mods().Single(m => m.Id == "Corona").Raw
            .Find("Packages:Package:Manifest")!.Value
            .Should().Be("corona:manifest-3258");
    }

    [TestMethod]
    public void Resolve_StubPointingAtLeaf_LoadsLeafAndAlignsLocalId()
    {
        var root = TempDir();
        try
        {
            var modDir = Path.Combine(root, "mod");
            WriteFile(modDir, "mod.xml", """
<Metadata>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Packages><Package Version="1.0"><Manifest>manifest-1</Manifest></Package></Packages>
  </Mod>
  <Manifest ID="manifest-1" Source="manifests/1.0.xml" />
</Metadata>
""");
            WriteFile(modDir, "manifests/1.0.xml", """
<Metadata>
  <Manifest ID="manifest-1" HashAlgorithm="CRC32C">
    <File Hash="3507CD54"><FileName>demo.lyi</FileName><RelativePath>/</RelativePath></File>
  </Manifest>
</Metadata>
""");

            var declarationPath = Path.Combine(modDir, "mod.xml");
            var loaded = MetadataBuilder.LoadSource(declarationPath);
            var package = loaded.Mods().Single().Packages.Single();

            var manifest = MetadataSourceManifest.Resolve(loaded, declarationPath, package.Raw);

            manifest.Should().NotBeNull();
            manifest!.Find("File:FileName")!.Value.Should().Be("demo.lyi");
            manifest.ToManifestEntry().Files.Single().Hash.Should().Be("3507CD54");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resolve_InlineManifest_ReturnsRegistrationWithoutReadingLeaf()
    {
        var root = TempDir();
        try
        {
            var modDir = Path.Combine(root, "mod");
            WriteFile(modDir, "mod.xml", """
<Metadata>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Packages><Package Version="1.0"><Manifest>manifest-1</Manifest></Package></Packages>
  </Mod>
  <Manifest ID="manifest-1" HashAlgorithm="CRC32C">
    <File Hash="3507CD54"><FileName>inline.lyi</FileName><RelativePath>/</RelativePath></File>
  </Manifest>
</Metadata>
""");

            var declarationPath = Path.Combine(modDir, "mod.xml");
            var loaded = MetadataBuilder.LoadSource(declarationPath);
            var package = loaded.Mods().Single().Packages.Single();

            var manifest = MetadataSourceManifest.Resolve(loaded, declarationPath, package.Raw);

            manifest!.Find("File:FileName")!.Value.Should().Be("inline.lyi");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resolve_MissingSourceOrMismatchedLeafId_Throws()
    {
        var root = TempDir();
        try
        {
            var modDir = Path.Combine(root, "missing");
            WriteFile(modDir, "mod.xml", """
<Metadata>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Packages><Package Version="1.0"><Manifest>manifest-1</Manifest></Package></Packages>
  </Mod>
  <Manifest ID="manifest-1" />
</Metadata>
""");
            var missingPath = Path.Combine(modDir, "mod.xml");
            var missing = MetadataBuilder.LoadSource(missingPath);
            var missingPackage = missing.Mods().Single().Packages.Single();
            var missingAct = () => MetadataSourceManifest.Resolve(missing, missingPath, missingPackage.Raw);
            missingAct.Should().Throw<InvalidOperationException>().WithMessage("*Source*");

            var mismatchDir = Path.Combine(root, "mismatch");
            WriteFile(mismatchDir, "mod.xml", """
<Metadata>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Packages><Package Version="1.0"><Manifest>wanted</Manifest></Package></Packages>
  </Mod>
  <Manifest ID="wanted" Source="manifests/1.0.xml" />
</Metadata>
""");
            WriteFile(mismatchDir, "manifests/1.0.xml",
                "<Metadata><Manifest ID=\"other\" HashAlgorithm=\"CRC32C\" /></Metadata>");
            var mismatchPath = Path.Combine(mismatchDir, "mod.xml");
            var mismatch = MetadataBuilder.LoadSource(mismatchPath);
            var mismatchPackage = mismatch.Mods().Single().Packages.Single();
            var mismatchAct = () => MetadataSourceManifest.Resolve(mismatch, mismatchPath, mismatchPackage.Raw);
            mismatchAct.Should().Throw<InvalidOperationException>().WithMessage("*不一致*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void ParseManifest_AcceptsNodeAndText_AndRejectsOtherRoots()
    {
        var node = XElement.Parse("""
<Manifest ID="m1" HashAlgorithm="SHA256">
  <File Hash="AABB"><FileName>a.lyi</FileName><RelativePath>data</RelativePath></File>
  <Dependencies><Dll Name="Lyi.dll" Hash="CCDD" /></Dependencies>
</Manifest>
""");

        var entry = MetadataParser.ParseManifest(node);
        entry.Id.Should().Be("m1");
        entry.HashAlgorithm.Should().Be("SHA256");
        entry.Files.Single().FileName.Should().Be("a.lyi");
        entry.Dependencies.Single().Name.Should().Be("Lyi.dll");

        MetadataParser.ParseManifest(node.ToString()).Files.Single().RelativePath.Should().Be("data");
        MetadataParser.ParseManifest("<Metadata>" + node + "</Metadata>").Id.Should().Be("m1");

        var wrongRoot = () => MetadataParser.ParseManifest(XElement.Parse("<Metadata />"));
        wrongRoot.Should().Throw<ArgumentException>();

        var noManifest = () => MetadataParser.ParseManifest("<Metadata><Mod ID=\"a\" /></Metadata>");
        noManifest.Should().Throw<ArgumentException>();
    }
}
