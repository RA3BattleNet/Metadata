using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 叶子清单的类型化解析：ManifestEntry / ManifestFileEntry / ManifestSourceEntry / ManifestDllEntry。
/// </summary>
[TestClass]
public class ManifestParseTests
{
    private static string TestDataPath => Path.Combine(AppContext.BaseDirectory, "TestData");

    private static Metadata LoadManifestNode(string fileName)
    {
        var node = Metadata.LoadFromFile(Path.Combine(TestDataPath, fileName)).Find("Manifest");
        node.Should().NotBeNull();
        return node!;
    }

    [TestMethod]
    public void ToManifestEntry_ExplicitAlgorithm_ParsesFilesSourcesAndDependencies()
    {
        var entry = LoadManifestNode("manifest-full.xml").ToManifestEntry();

        entry.Id.Should().Be("manifest-full");
        entry.HashAlgorithm.Should().Be("SHA256");
        entry.Files.Should().HaveCount(2);

        var first = entry.Files[0];
        first.FileName.Should().Be("corona.lyi");
        first.RelativePath.Should().Be("/");
        first.Hash.Should().Be("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
        first.Size.Should().Be(1024);
        first.DownloadName.Should().Be("corona.zst");
        first.Compression.Should().Be("zstd");
        first.KindOf.Should().Be("MOD; ENCRYPTED_FILE; CAN_PATCH;");
        first.Sources.Should().HaveCount(2);
        first.Sources[0].Type.Should().Be("HTTP");
        first.Sources[0].Url.Should().Be("https://example.com/files/corona.lyi");
        first.Sources[1].Type.Should().Be("BT");
        first.Sources[1].Url.Should().EndWith(".torrent");
        first.Raw.Name.Should().Be("File");

        entry.Files[1].Size.Should().BeNull("Size 缺失返回 null");
        entry.Files[1].DownloadName.Should().BeNull("DownloadName 缺失返回 null");
        entry.Files[1].Compression.Should().BeNull("Compression 缺失返回 null");
        entry.Files[1].Sources.Should().HaveCount(1);

        entry.Dependencies.Should().HaveCount(1);
        entry.Dependencies[0].Name.Should().Be("NativeDll.dll");
        entry.Dependencies[0].Version.Should().Be("1.5.5.2");
        entry.Dependencies[0].KindOf.Should().Be("APPLICATION");
    }
    [TestMethod]
    public void ToManifestEntry_MissingMount_DefaultsToBase()
    {
        var entry = LoadManifestNode("manifest-full.xml").ToManifestEntry();

        entry.Files.Should().OnlyContain(file => file.Mount == "base" && file.Language == null && file.Package == null);
    }

    [TestMethod]
    public void ToManifestEntry_ExplicitMount_ReadsLanguageAndPackage()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-mount-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(temp, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-mount" HashAlgorithm="CRC32C">
    <File Hash="9B623C7C" Mount="LANGUAGE" Language="chs">
      <FileName>chs.big</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
    <File Hash="9B623C7C" Mount="optional" Package="crates">
      <FileName>crates.big</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");
            var entry = Metadata.LoadFromFile(temp).Find("Manifest")!.ToManifestEntry();
            entry.Files[0].Mount.Should().Be("language");
            entry.Files[0].Language.Should().Be("chs");
            entry.Files[0].Package.Should().BeNull();
            entry.Files[1].Mount.Should().Be("optional");
            entry.Files[1].Package.Should().Be("crates");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }


    [TestMethod]
    public void ToManifestEntry_MissingAlgorithm_DefaultsToCrc32C()
    {
        var entry = LoadManifestNode("manifest-default.xml").ToManifestEntry();

        entry.HashAlgorithm.Should().Be("CRC32C");
        entry.Files.Should().HaveCount(1);
        entry.Files[0].Size.Should().BeNull();
        entry.Files[0].Sources.Should().HaveCount(1);
        entry.Dependencies.Should().HaveCount(1);
        entry.Dependencies[0].Hash.Should().HaveLength(8);
        entry.Dependencies[0].Version.Should().BeNull();
        entry.Dependencies[0].KindOf.Should().BeNull();
    }

    [TestMethod]
    public void ToManifestEntry_LegacyManifest_EmptySourcesAndNoDependencies()
    {
        var entry = LoadManifestNode("manifest-legacy.xml").ToManifestEntry();

        entry.HashAlgorithm.Should().Be("CRC32C");
        entry.Files.Should().HaveCount(1);
        entry.Files[0].FileName.Should().Be("corona_3.229.lyi");
        entry.Files[0].Sources.Should().BeEmpty();
        entry.Files[0].Size.Should().BeNull();
        entry.Dependencies.Should().BeEmpty();
    }

    [TestMethod]
    public void ToManifestEntry_LowercaseSourceType_NormalizedToUpper()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-case-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(temp, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-case" HashAlgorithm="CRC32C">
    <File Hash="9B623C7C">
      <FileName>a.bin</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
      <Sources>
        <Source Type="http" Url="https://example.com/a.bin" />
        <Source Type="bt" Url="https://example.com/a.bin.torrent" />
      </Sources>
    </File>
  </Manifest>
</Metadata>
""");
            var entry = Metadata.LoadFromFile(temp).Find("Manifest")!.ToManifestEntry();
            entry.Files[0].Sources.Select(s => s.Type).Should().Equal("HTTP", "BT");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [TestMethod]
    public void ToManifestEntry_UnknownAlgorithm_Throws()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-algo-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(temp, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-unknown" HashAlgorithm="CRC64">
    <File Hash="ABCD">
      <FileName>a.bin</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");
            var node = Metadata.LoadFromFile(temp).Find("Manifest")!;
            var act = () => node.ToManifestEntry();
            act.Should().Throw<InvalidOperationException>().WithMessage("*HashAlgorithm*");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [TestMethod]
    public void ManifestRegistration_ResolvesRegisteredId_AndNullWhenMissing()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));
        var dst = Path.Combine(Path.GetTempPath(), $"manifest-reg-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(src, dst, contentRevision: "manifest-reg");
            var root = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));

            var registered = root.GetAllElements("Manifest")
                .First(m => MetadataFlattener.LocalId(m.Get("ID")) == "manifest-3258");
            var found = root.ManifestRegistration(registered.Get("ID")!);
            found.Should().BeSameAs(registered);
            found!.Get("Source").Should().EndWith("manifests/3.258.xml");

            root.ManifestRegistration("no-such-manifest").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }
}
