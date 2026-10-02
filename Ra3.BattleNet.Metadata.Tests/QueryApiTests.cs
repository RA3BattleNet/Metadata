using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 共享库的查询面：列出全部 Mod / Application、接 LINQ 组合、按 ID 取实体，以及实体上的导航。
/// </summary>
[TestClass]
public class QueryApiTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    private static Metadata LoadFlattened(out string outputDir)
    {
        outputDir = Path.Combine(Path.GetTempPath(), $"query-api-{Guid.NewGuid():N}");
        MetadataBuilder.Build(RepoMetadataDir, outputDir, contentRevision: "query-api");
        return MetadataBuilder.Load(Path.Combine(outputDir, "metadata.xml"));
    }

    [TestMethod]
    public void ModsAndApplications_ListEveryEntity_AndComposeWithLinq()
    {
        // 自足数据：断言的是「列出全部 + 可接 LINQ」，不跟仓库数据内容绑定
        var path = Path.Combine(Path.GetTempPath(), $"query-api-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata SchemaVersion="1.0">
  <Mod ID="Alpha"><CurrentVersion>1.0</CurrentVersion></Mod>
  <Mod ID="Beta"><CurrentVersion>2.0</CurrentVersion></Mod>
  <Application ID="Launcher"><Version>9.9</Version></Application>
</Metadata>
""");
            var root = Metadata.LoadFromFile(path);

            root.Mods().Select(m => m.Id).Should().Equal("Alpha", "Beta");
            root.Applications().Select(a => a.Id).Should().Equal("Launcher");

            root.Mods()
                .Where(m => m.Version != "2.0")
                .OrderByDescending(m => m.Id)
                .Select(m => m.Id)
                .Should().Equal("Alpha");

            root.Catalog().Mod("alpha").Should().NotBeNull("按 ID 查大小写不敏感");
            root.Catalog().Mod("no-such-mod").Should().BeNull();
            root.Catalog().Application("launcher").Should().NotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Mod_ManifestSource_ResolvesRegisteredLeafManifest()
    {
        var root = LoadFlattened(out var dst);
        try
        {
            var corona = root.Catalog().Mod("Corona")!;

            corona.Package().Should().NotBeNull("版本缺省取实体当前版本");
            corona.ManifestSource().Replace('\\', '/').Should().EndWith("manifests/3.258.xml");

            var act = () => corona.ManifestSource("9.9.9");
            act.Should().Throw<InvalidOperationException>().WithMessage("*没有版本*");
        }
        finally
        {
            Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Application_ChangelogSource_PicksRequestedLanguage()
    {
        var root = LoadFlattened(out var dst);
        try
        {
            var app = root.Catalog().Application("RA3BattleNet")!;

            app.ChangelogSource("zh-CN").Should().NotBeNull().And.EndWith("changelogs/zh-1.9.9.11.md");
            app.ChangelogSource("en-US").Should().NotBeNull().And.EndWith("changelogs/en-1.9.9.11.md");
            app.ChangelogSource("ja-JP").Should().BeNull("没配这门语言就没有更新日志");
        }
        finally
        {
            Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void MetadataSchema_IsCompatible_AcceptsCurrentContractOnly()
    {
        MetadataSchema.Current.Should().Be("1.0");
        MetadataSchema.IsCompatible("1.0").Should().BeTrue();
        MetadataSchema.IsCompatible("2.0").Should().BeFalse();
        MetadataSchema.IsCompatible(null).Should().BeFalse();
    }

    [TestMethod]
    public void MetadataResourceUri_ResolvesSourceAgainstEntryDirectory()
    {
        MetadataResourceUri
            .Resolve("https://metadata.ra3battle.net/metadata.xml", "apps/ra3battlenet/changelogs/zh-1.5.5.2.md")
            .AbsoluteUri.Should().Be("https://metadata.ra3battle.net/apps/ra3battlenet/changelogs/zh-1.5.5.2.md");

        MetadataResourceUri
            .Resolve("https://x/metadata.xml", @"mods\corona\images\icon.webp")
            .AbsoluteUri.Should().Be("https://x/mods/corona/images/icon.webp");
    }
}
