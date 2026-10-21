using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 缓存磁盘 JSON（<c>origin.json</c> 与 <c>.etag</c>）是跨版本契约：源生成上下文写出的字节
/// 必须与旧运行时反射实现逐字节一致——默认属性命名、null 属性照常写出、无缩进、默认转义。
/// </summary>
[TestClass]
public class CacheJsonContractTests
{
    [TestMethod]
    public void Origin_SerializesToLegacyBytes()
    {
        var json = JsonSerializer.Serialize(
            new CacheOrigin { PublishingBase = "https://metadata.example/" },
            MetadataCacheJsonContext.Default.CacheOrigin);

        Encoding.UTF8.GetBytes(json).Should().Equal(
            Encoding.UTF8.GetBytes("""{"PublishingBase":"https://metadata.example/"}"""));
    }

    [TestMethod]
    public void Stamp_FullFields_SerializeToLegacyBytesInDeclarationOrder()
    {
        var json = JsonSerializer.Serialize(
            new CacheStamp
            {
                Uri = "https://metadata.example/apps/image.png",
                ETag = "\"v1\"",
                LastModified = "Mon, 01 Jan 2024 00:00:00 GMT",
                Sha256 = "abcdef",
                ContentType = "image/png",
            },
            MetadataCacheJsonContext.Default.CacheStamp);

        Encoding.UTF8.GetBytes(json).Should().Equal(Encoding.UTF8.GetBytes(
            """{"Uri":"https://metadata.example/apps/image.png","ETag":"\u0022v1\u0022","LastModified":"Mon, 01 Jan 2024 00:00:00 GMT","Sha256":"abcdef","ContentType":"image/png"}"""));
    }

    [TestMethod]
    public void Stamp_XmlBody_KeepsNullFieldsAndFieldOrder()
    {
        var json = JsonSerializer.Serialize(
            new CacheStamp
            {
                Uri = "https://metadata.example/apps/leaf.xml",
                ETag = "\"v1\"",
                Sha256 = "abcdef",
            },
            MetadataCacheJsonContext.Default.CacheStamp);

        Encoding.UTF8.GetBytes(json).Should().Equal(Encoding.UTF8.GetBytes(
            """{"Uri":"https://metadata.example/apps/leaf.xml","ETag":"\u0022v1\u0022","LastModified":null,"Sha256":"abcdef","ContentType":null}"""));
    }

    [TestMethod]
    public void Context_DeserializesLegacyFilesCaseInsensitively()
    {
        var origin = JsonSerializer.Deserialize(
            """{"publishingBase":"https://metadata.example/"}""",
            MetadataCacheJsonContext.Default.CacheOrigin);

        origin.Should().NotBeNull();
        origin!.PublishingBase.Should().Be("https://metadata.example/");

        var stamp = JsonSerializer.Deserialize(
            """{"uri":"https://metadata.example/apps/leaf.xml","etag":"\"v1\"","sha256":"abcdef"}""",
            MetadataCacheJsonContext.Default.CacheStamp);

        stamp.Should().NotBeNull();
        stamp!.Uri.Should().Be("https://metadata.example/apps/leaf.xml");
        stamp.ETag.Should().Be("\"v1\"");
        stamp.Sha256.Should().Be("abcdef");
        stamp.LastModified.Should().BeNull();
        stamp.ContentType.Should().BeNull();
    }
}
