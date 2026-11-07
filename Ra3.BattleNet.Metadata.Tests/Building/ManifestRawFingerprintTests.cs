using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class ManifestRawFingerprintTests
{
    private static string Manifest(string attributes, string algorithm = "CRC32C") => $"""
        <Manifest ID="raw-fingerprint" HashAlgorithm="{algorithm}">
          <File Hash="9B623C7C" {attributes}>
            <FileName>a.zip</FileName><RelativePath>/</RelativePath><KindOf>RUNTIME;</KindOf>
          </File>
        </Manifest>
        """;

    [TestMethod]
    [DataRow("RawHashAlgorithm=\"SHA256\"")]
    [DataRow("RawHash=\"12345678\" RawHashAlgorithm=\"MD5\"")]
    [DataRow("RawHash=\"12345678\" RawHashAlgorithm=\"None\"")]
    [DataRow("RawHash=\"12345678\" RawHashAlgorithm=\"SHA256\"")]
    [DataRow("RawHash=\"ZZZZZZZZ\"")]
    [DataRow("RawHash=\"\"")]
    public void Parse_InvalidRawFingerprint_Throws(string attributes)
    {
        var act = () => MetadataParser.ParseManifest(Manifest(attributes));
        act.Should().Throw<InvalidOperationException>().WithMessage("*RawHash*");
    }

    [TestMethod]
    [DataRow("CRC32C", "3507CD54")]
    [DataRow("MD5", "d41d8cd98f00b204e9800998ecf8427e")]
    [DataRow("SHA256", "b601d79b38720d174c17b6affed3506f01c5033d20a42d1f90fe5745f1b8d021")]
    public void Parse_OmittedRawAlgorithm_InheritsManifest(string algorithm, string hash)
    {
        var file = MetadataParser.ParseManifest(Manifest($"RawHash=\"{hash}\"", algorithm)).Files.Single();
        file.RawHash.Should().Be(hash);
        file.RawHashAlgorithm.Should().Be(algorithm);
        file.Raw.Get("RawHashAlgorithm").Should().BeNull("投影不改写原始声明");
    }

    [TestMethod]
    public void Parse_RawHashCannotInheritNone()
    {
        var act = () => MetadataParser.ParseManifest(Manifest("RawHash=\"3507CD54\"", "None"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*不能继承 None*");
    }

    [TestMethod]
    public void Parse_NoRawFingerprint_PreservesLegacyBehavior()
    {
        var file = MetadataParser.ParseManifest(Manifest("")).Files.Single();
        file.RawHash.Should().BeNull();
        file.RawHashAlgorithm.Should().BeNull();
        file.Hash.Should().Be("9B623C7C");
    }
}
