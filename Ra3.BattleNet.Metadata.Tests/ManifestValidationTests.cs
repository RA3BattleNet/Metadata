using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 新格式清单的构建期硬校验：声明新格式才触发，旧清单不触发。
/// </summary>
[TestClass]
public class ManifestValidationTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    private static void SeedSchemas(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
        {
            File.Copy(Path.Combine(RepoMetadataDir, name), Path.Combine(targetDir, name), overwrite: true);
        }
    }

    private static string ManifestXml(string body, string hashAlgorithm = "CRC32C")
    {
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-bad" HashAlgorithm="{hashAlgorithm}">
{body}
  </Manifest>
</Metadata>
""";
    }

    private static string FileBlock(
        string hash,
        string sources,
        string fileName = "a.bin",
        string relativePath = "/",
        string sizeAttribute = "")
    {
        return $"""
    <File Hash="{hash}"{sizeAttribute}>
      <FileName>{fileName}</FileName>
      <RelativePath>{relativePath}</RelativePath>
      <KindOf>MOD;</KindOf>
      {sources}
    </File>
""";
    }

    /// <summary>带下载名与压缩声明的文件块。</summary>
    private static string CompressedFileBlock(string downloadName, string compression, string fileName = "a.bin")
    {
        return $"""
    <File Hash="9B623C7C" DownloadName="{downloadName}" Compression="{compression}">
      <FileName>{fileName}</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
      {HttpSource("https://example.com/a.zst")}
    </File>
""";
    }

    private static string HttpSource(string url) => $"<Sources><Source Type=\"HTTP\" Url=\"{url}\" /></Sources>";

    private static string BtSource(string url) => $"<Sources><Source Type=\"BT\" Url=\"{url}\" /></Sources>";
    private static string MountedFileBlock(string mountAttributes) => $"""
    <File Hash="9B623C7C"{mountAttributes}>
      <FileName>a.bin</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
      {HttpSource("https://example.com/a.bin")}
    </File>
""";

    /// <summary>Skudef 块；必须写在 File 表前面，故与 FileBlock 拼接时放在前面。</summary>
    private static string SkudefBlock(string body, string gameVersion = "1.12")
        => $"<Skudef GameVersion=\"{gameVersion}\">{body}</Skudef>\n";

    private static string SingleFile(string fileName = "a.bin", string relativePath = "/")
        => FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"), fileName: fileName, relativePath: relativePath);

    /// <summary>写临时源树（schema + 入口 + 一个清单文件）跑构建，断言硬失败且消息带来源路径与 Manifest ID。</summary>
    private static void AssertBuildFails(string manifestXml, params string[] fragments)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-bad-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), "<Metadata />");
            File.WriteAllText(Path.Combine(src, "manifest.xml"), manifestXml);

            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "x");
            var ex = act.Should().Throw<InvalidOperationException>().Which;
            ex.Message.Should().Contain("manifest.xml", "消息必须带来源文件路径");
            ex.Message.Should().Contain("manifest-bad", "消息必须带 Manifest ID");
            foreach (var fragment in fragments)
                ex.Message.Should().Contain(fragment);
            Directory.Exists(dst).Should().BeFalse("半残输出应被清理");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_PlaceholderHash_HardFails()
    {
        AssertBuildFails(ManifestXml(FileBlock("AAAAAAAA", HttpSource("https://example.com/a.bin"))), "占位值");
    }

    [TestMethod]
    public void Build_HashLengthMismatch_HardFails()
    {
        AssertBuildFails(ManifestXml(FileBlock("ABCD", HttpSource("https://example.com/a.bin"))), "长度应为 8");
    }

    [TestMethod]
    public void Build_FileWithoutSources_HardFails()
    {
        AssertBuildFails(ManifestXml(FileBlock("9B623C7C", string.Empty)), "缺少 Source");
    }

    [TestMethod]
    public void Build_BtUrlWithoutTorrentSuffix_HardFails()
    {
        AssertBuildFails(ManifestXml(FileBlock("9B623C7C", BtSource("https://example.com/a.bin"))), ".torrent");
    }

    [TestMethod]
    public void Build_HttpUrlNotAbsolute_HardFails()
    {
        AssertBuildFails(ManifestXml(FileBlock("9B623C7C", HttpSource("ftp://example.com/a.bin"))), "http/https");
    }

    [TestMethod]
    public void Build_NonPositiveSize_HardFails()
    {
        AssertBuildFails(
            ManifestXml(FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"), sizeAttribute: " Size=\"0\"")),
            "Size 必须是正整数");
    }

    [TestMethod]
    public void Build_DuplicateFileNameAndRelativePath_HardFails()
    {
        var body = FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"))
            + FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"));
        AssertBuildFails(ManifestXml(body), "重复");
    }

    [TestMethod]
    public void Build_DuplicateDllName_HardFails()
    {
        var body = FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"))
            + """
    <Dependencies>
      <Dll Name="NativeDll.dll" Hash="9B623C7C" />
      <Dll Name="NativeDll.dll" Hash="9B623C7C" />
    </Dependencies>
""";
        AssertBuildFails(ManifestXml(body), "Dll Name 重复");
    }

    [TestMethod]
    public void Build_RelativePathWithParentPrefix_HardFails()
    {
        AssertBuildFails(
            ManifestXml(FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"), relativePath: "../evil/")),
            "必须是相对路径");
    }

    [TestMethod]
    public void Build_RelativePathAbsoluteDrive_HardFails()
    {
        AssertBuildFails(
            ManifestXml(FileBlock("9B623C7C", HttpSource("https://example.com/a.bin"), relativePath: "C:/evil/")),
            "必须是相对路径");
    }

    [TestMethod]
    public void Build_UnknownCompression_HardFails()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-comp-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), "<Metadata />");
            File.WriteAllText(Path.Combine(src, "manifest.xml"), ManifestXml(CompressedFileBlock("a.zst", "lz4")));

            // Compression 的取值由 XSD 枚举兜住，构建在 schema 阶段就失败
            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "x");
            act.Should().Throw<InvalidOperationException>().WithMessage("*Compression*");
            Directory.Exists(dst).Should().BeFalse("半残输出应被清理");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_CompressionWithoutDownloadName_HardFails()
    {
        AssertBuildFails(
            ManifestXml($"""
    <File Hash="9B623C7C" Compression="zstd">
      <FileName>a.lyi</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
      {HttpSource("https://example.com/a.zst")}
    </File>
"""),
            "必须写 DownloadName");
    }

    [TestMethod]
    public void Build_DownloadNameEqualsFileName_HardFails()
    {
        AssertBuildFails(
            ManifestXml(CompressedFileBlock("a.bin", "zstd", fileName: "a.bin")),
            "不能同名");
    }

    [TestMethod]
    public void Build_DownloadNameWithPathSeparator_HardFails()
    {
        AssertBuildFails(
            ManifestXml(CompressedFileBlock("sub/a.zst", "zstd")),
            "不含路径分隔符");
    }

    [TestMethod]
    public void Build_LanguageMountWithoutLanguage_HardFails()
    {
        AssertBuildFails(ManifestXml(MountedFileBlock(" Mount=\"language\"")), "必须写 Language");
    }

    [TestMethod]
    public void Build_OptionalMountWithoutPackage_HardFails()
    {
        AssertBuildFails(ManifestXml(MountedFileBlock(" Mount=\"optional\"")), "必须写 Package");
    }

    [TestMethod]
    public void Build_BaseMountWithLanguage_HardFails()
    {
        AssertBuildFails(ManifestXml(MountedFileBlock(" Mount=\"base\" Language=\"chs\"")), "不能带 Language");
    }

    [TestMethod]
    public void Build_MountTokenWithSlash_HardFails()
    {
        AssertBuildFails(ManifestXml(MountedFileBlock(" Mount=\"language\" Language=\"ch/s\"")), "Language");
    }

    [TestMethod]
    public void Build_CompressedFile_Succeeds()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-zstd-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), "<Metadata />");
            File.WriteAllText(
                Path.Combine(src, "manifest.xml"),
                ManifestXml(CompressedFileBlock("a.zst", "zstd", fileName: "a.lyi")));

            MetadataBuilder.Build(src, dst, contentRevision: "x");
            File.Exists(Path.Combine(dst, "metadata.xml")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_NewFormatManifest_Succeeds()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-ok-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), "<Metadata />");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "TestData", "manifest-full.xml"),
                Path.Combine(src, "manifest.xml"));

            MetadataBuilder.Build(src, dst, contentRevision: "x");
            File.Exists(Path.Combine(dst, "metadata.xml")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_LegacyManifest_StillSucceeds()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-legacy-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), "<Metadata />");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "TestData", "manifest-legacy.xml"),
                Path.Combine(src, "manifest.xml"));

            MetadataBuilder.Build(src, dst, contentRevision: "x");
            File.Exists(Path.Combine(dst, "metadata.xml")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_SkudefWithFileMount_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"a.bin\" />") + MountedFileBlock(" Mount=\"optional\" Package=\"hd-shadow\"");
        AssertBuildFails(ManifestXml(body), "不能再写 Mount/Language/Package");
    }

    [TestMethod]
    public void Build_AddBigDanglingFile_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"missing.bin\" />") + SingleFile();
        AssertBuildFails(ManifestXml(body), "引用了不存在的 FileName");
    }

    [TestMethod]
    public void Build_FileNotReferencedByAddBig_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"b.bin\" />") + SingleFile() + SingleFile("b.bin");
        AssertBuildFails(ManifestXml(body), "没有被 AddBig 引用");
    }

    [TestMethod]
    public void Build_FileNameReferencedTwice_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"a.bin\" /><AddBig File=\"a.bin\" />") + SingleFile();
        AssertBuildFails(ManifestXml(body), "被多条 AddBig 引用");
    }

    [TestMethod]
    public void Build_DuplicateFileNameWithSkudef_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"a.bin\" />") + SingleFile() + SingleFile(relativePath: "/sub/");
        AssertBuildFails(ManifestXml(body), "FileName 重复");
    }

    [TestMethod]
    public void Build_AddBigWithLanguageAndPackage_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"a.bin\" Language=\"en\" Package=\"hd-shadow\" />") + SingleFile();
        AssertBuildFails(ManifestXml(body), "同时写了 Language 与 Package");
    }

    [TestMethod]
    public void Build_AddConfigWithPathSeparator_HardFails()
    {
        var body = SkudefBlock("<AddConfig LocalFile=\"sub/Config.txt\" /><AddBig File=\"a.bin\" />") + SingleFile();
        AssertBuildFails(ManifestXml(body), "必须是纯文件名");
    }

    [TestMethod]
    public void Build_EmptySkudef_HardFails()
    {
        var body = SkudefBlock(string.Empty) + SingleFile();
        AssertBuildFails(ManifestXml(body), "没有任何指令");
    }

    [TestMethod]
    public void Build_BadGameVersion_HardFails()
    {
        var body = SkudefBlock("<AddBig File=\"a.bin\" />", gameVersion: "v1") + SingleFile();
        AssertBuildFails(ManifestXml(body), "GameVersion 非法");
    }

    [TestMethod]
    public void Build_SkudefManifest_Succeeds()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"manifest-skudef-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), "<Metadata />");
            File.WriteAllText(
                Path.Combine(src, "manifest.xml"),
                ManifestXml(SkudefBlock(
                    "<AddConfig LocalFile=\"CustomConfig.txt\" Optional=\"1\" />"
                    + "<AddBig File=\"a.bin\" Package=\"hd-shadow\" />") + SingleFile()));

            MetadataBuilder.Build(src, dst, contentRevision: "x");
            File.Exists(Path.Combine(dst, "metadata.xml")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
