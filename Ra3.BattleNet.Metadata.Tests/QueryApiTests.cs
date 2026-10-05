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
    public void Build_PackagesAndPosts_AreIndependent()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"query-posts-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        const string newsZh = "新闻中文正文。\n";
        const string newsEn = "English news body.\n";
        const string mixedZh = "带简介的中文正文。\n";
        const string mixedEn = "Described English body.\n";
        const string bareZh = "无简介中文正文。\n";
        const string bareEn = "Bare English body.\n";

        try
        {
            Directory.CreateDirectory(src);
            foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
                File.Copy(Path.Combine(RepoMetadataDir, name), Path.Combine(src, name), overwrite: true);

            File.WriteAllText(Path.Combine(src, "news-zh.md"), newsZh);
            File.WriteAllText(Path.Combine(src, "news-en.md"), newsEn);
            File.WriteAllText(Path.Combine(src, "mixed-zh.md"), mixedZh);
            File.WriteAllText(Path.Combine(src, "mixed-en.md"), mixedEn);
            File.WriteAllText(Path.Combine(src, "bare-zh.md"), bareZh);
            File.WriteAllText(Path.Combine(src, "bare-en.md"), bareEn);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Markdown ID="news-only-zh" Source="news-zh.md" Hash="${MD5::}"/>
  <Markdown ID="news-only-en" Source="news-en.md" Hash="${MD5::}"/>
  <Markdown ID="mixed-zh" Source="mixed-zh.md" Hash="${MD5::}"/>
  <Markdown ID="mixed-en" Source="mixed-en.md" Hash="${MD5::}"/>
  <Markdown ID="bare-zh" Source="bare-zh.md" Hash="${MD5::}"/>
  <Markdown ID="bare-en" Source="bare-en.md" Hash="${MD5::}"/>
  <Mod ID="PackageOnly">
    <CurrentVersion>9.0</CurrentVersion>
    <DisplayName Language="zh">仅版本包</DisplayName>
    <DisplayName Language="en">Package Only</DisplayName>
    <Packages>
      <Package Version="9.0" />
    </Packages>
  </Mod>
  <Application ID="NewsOnly">
    <DisplayName Language="zh">仅新闻</DisplayName>
    <DisplayName Language="en">News Only</DisplayName>
    <Posts>
      <Post DateTime="2026-10-02T00:00:00+08:00">
        <Titles>
          <Title Language="zh-CN">独立新闻</Title>
          <Title Language="en-US">Standalone news</Title>
        </Titles>
        <Descriptions>
          <Description Language="zh-CN">中文简介。</Description>
          <Description Language="en-US">English blurb.</Description>
        </Descriptions>
        <Contents>
          <Content Language="zh-CN">news-only-zh</Content>
          <Content Language="en-US">news-only-en</Content>
        </Contents>
      </Post>
    </Posts>
  </Application>
  <Application ID="Mixed">
    <Version>9.0</Version>
    <DisplayName Language="zh">混合</DisplayName>
    <DisplayName Language="en">Mixed</DisplayName>
    <Packages>
      <Package Version="9.0" />
    </Packages>
    <Posts>
      <Post DateTime="2026-01-01T00:00:00+08:00">
        <Titles>
          <Title Language="zh-CN">版本说明</Title>
          <Title Language="en-US">Update 1.0</Title>
        </Titles>
        <Descriptions>
          <Description Language="zh-CN">有简介。</Description>
          <Description Language="en-US">Has a blurb.</Description>
        </Descriptions>
        <Contents>
          <Content Language="zh-CN">mixed-zh</Content>
          <Content Language="en-US">mixed-en</Content>
        </Contents>
      </Post>
      <Post DateTime="2026-02-02T00:00:00+08:00">
        <Titles>
          <Title Language="zh-CN">无简介</Title>
          <Title Language="en-US">No blurb</Title>
        </Titles>
        <Contents>
          <Content Language="zh-CN">bare-zh</Content>
          <Content Language="en-US">bare-en</Content>
        </Contents>
      </Post>
    </Posts>
  </Application>
</Metadata>
""");

            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "posts-independent");
            var loaded = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));
            loaded.Get("SchemaVersion").Should().Be("1.0");
            loaded.Get("ContentRevision").Should().Be("posts-independent");

            var packageOnly = loaded.Catalog().Mod("PackageOnly")!;
            packageOnly.Version.Should().Be("9.0");
            packageOnly.Package()!.Version.Should().Be("9.0");
            packageOnly.Package()!.ManifestId.Should().BeNull();
            packageOnly.Raw.GetAllElements("Post").Should().BeEmpty();

            var newsOnly = loaded.Catalog().Application("NewsOnly")!;
            newsOnly.Version.Should().BeNull();
            newsOnly.Packages.Should().BeEmpty();
            var newsPost = newsOnly.Raw.Find("Posts")!.Children.Where(c => c.Name == "Post").Should().ContainSingle().Which;
            newsPost.GetAllElements("Description").Select(d => d.Value).Should().Equal("中文简介。", "English blurb.");
            AssertPublishedBody(loaded, dst, ContentId(newsPost, "zh-CN"), newsZh);
            AssertPublishedBody(loaded, dst, ContentId(newsPost, "en-US"), newsEn);

            var mixed = loaded.Catalog().Application("Mixed")!;
            mixed.Version.Should().Be("9.0");
            mixed.Packages.Select(p => p.Version).Should().Equal("9.0");
            mixed.Packages.Should().NotContain(p => p.Version == "1.0");
            var posts = mixed.Raw.Find("Posts")!.Children.Where(c => c.Name == "Post").ToList();
            posts.Should().HaveCount(2);
            var described = posts.Single(p => p.GetAllElements("Title").Any(t => t.Value == "Update 1.0"));
            described.Find("Descriptions").Should().NotBeNull();
            var bare = posts.Single(p => p.Find("Descriptions") == null);
            bare.GetAllElements("Description").Should().BeEmpty();
            AssertPublishedBody(loaded, dst, ContentId(described, "zh-CN"), mixedZh);
            AssertPublishedBody(loaded, dst, ContentId(described, "en-US"), mixedEn);
            AssertPublishedBody(loaded, dst, ContentId(bare, "zh-CN"), bareZh);
            AssertPublishedBody(loaded, dst, ContentId(bare, "en-US"), bareEn);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }

        static string ContentId(Metadata post, string language) =>
            post.GetAllElements("Content").Single(c => c.Get("Language") == language).Value!;

        static void AssertPublishedBody(Metadata loaded, string outputDir, string contentId, string expected)
        {
            contentId.Should().Contain(":");
            var markdown = loaded.Markdowns().Single(m => m.Id == contentId);
            var relative = markdown.Source!.Replace('/', Path.DirectorySeparatorChar);
            File.ReadAllText(Path.Combine(outputDir, relative)).Should().Be(expected);
        }
    }

    [TestMethod]
    public void MetadataSchema_IsCompatible_AcceptsCurrentContractOnly()
    {
        MetadataSchema.IsCompatible(MetadataSchema.Current).Should().BeTrue();
        MetadataSchema.IsCompatible("1.0").Should().BeTrue();
        MetadataSchema.IsCompatible("2.0").Should().BeFalse();
        MetadataSchema.IsCompatible("3.0").Should().BeFalse();
        MetadataSchema.IsCompatible(null).Should().BeFalse();
    }

    [TestMethod]
    public void MetadataResourceUri_ResolvesSourceAgainstEntryDirectory()
    {
        MetadataResourceUri
            .Resolve("https://metadata.ra3battle.net/metadata.xml", "apps/ra3battlenet/posts/news-zh-1.9.9.11.md")
            .AbsoluteUri.Should().Be("https://metadata.ra3battle.net/apps/ra3battlenet/posts/news-zh-1.9.9.11.md");

        MetadataResourceUri
            .Resolve("https://x/metadata.xml", @"mods\corona\images\icon.webp")
            .AbsoluteUri.Should().Be("https://x/mods/corona/images/icon.webp");
    }
}
