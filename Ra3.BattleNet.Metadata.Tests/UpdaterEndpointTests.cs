using Ra3.BattleNet.Metadata;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 更新线投影：只认第一个直接子级 UpdateKind，再在其直接子级里等值匹配 Current。
/// </summary>
[TestClass]
public class UpdaterEndpointTests
{
    [TestMethod]
    public void ResolveUpdaterEndpoint_UsesFirstDirectKind_AndResolvesRelativeSourceAgainstOrigin()
    {
        var path = Write("""
            <?xml version="1.0" encoding="utf-8"?>
            <Metadata SchemaVersion="1.0">
              <Application ID="RA3BattleNet">
                <UpdateKind ID="RA3BattleNet" Current="1.2.3" BaseUrl="  " FallbackBaseUrl="https://fb.example/" DisplayName=" 客户端 ">
                  <Updater Version="0.9.0" Source="https://update.example/old.xml" />
                  <Note>
                    <Updater Version="1.2.3" Source="https://nested.example/no.xml" />
                  </Note>
                  <Updater Version="1.2.3" Source="manifests/manifest-1.2.3.xml" />
                </UpdateKind>
                <UpdateKind ID="later" Current="9.9.9">
                  <Updater Version="9.9.9" Source="https://other.example/m.xml" />
                </UpdateKind>
              </Application>
            </Metadata>
            """);
        try
        {
            var app = Metadata.LoadFromFile(path).Catalog().Application("RA3BattleNet");
            var endpoint = app!.ResolveUpdaterEndpoint(new Uri("https://metadata.example/metadata.xml"));

            endpoint.Should().NotBeNull();
            endpoint!.Version.Should().Be("1.2.3");
            endpoint.ManifestUrl.Should().Be("https://metadata.example/manifests/manifest-1.2.3.xml");
            endpoint.BaseUrl.Should().BeNull();
            endpoint.FallbackBaseUrl.Should().Be("https://fb.example/");
            endpoint.DisplayName.Should().Be("客户端");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ResolveUpdaterEndpoint_MissingOrUnmatched_ReturnsNull()
    {
        var unmatched = Write("""
            <?xml version="1.0" encoding="utf-8"?>
            <Metadata SchemaVersion="1.0">
              <Application ID="RA3BattleNet">
                <UpdateKind ID="first" Current="1.0.0">
                  <Updater Version="2.0.0" Source="https://update.example/m.xml" />
                </UpdateKind>
                <UpdateKind ID="second" Current="2.0.0">
                  <Updater Version="2.0.0" Source="https://update.example/m.xml" />
                </UpdateKind>
              </Application>
            </Metadata>
            """);
        var absent = Write("""
            <?xml version="1.0" encoding="utf-8"?>
            <Metadata SchemaVersion="1.0">
              <Application ID="RA3BattleNet">
                <Version>1.0.0</Version>
              </Application>
            </Metadata>
            """);
        try
        {
            var origin = new Uri("https://metadata.example/metadata.xml");
            Metadata.LoadFromFile(unmatched).Catalog().Application("RA3BattleNet")!
                .ResolveUpdaterEndpoint(origin).Should().BeNull("第一个 UpdateKind 没有等值版本时不落到后面的更新线");
            Metadata.LoadFromFile(absent).Catalog().Application("RA3BattleNet")!
                .ResolveUpdaterEndpoint(origin).Should().BeNull();

            var app = Metadata.LoadFromFile(absent).Catalog().Application("RA3BattleNet")!;
            var act = () => app.ResolveUpdaterEndpoint(null!);
            act.Should().Throw<ArgumentNullException>();
        }
        finally
        {
            File.Delete(unmatched);
            File.Delete(absent);
        }
    }

    private static string Write(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"updater-endpoint-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }
}
