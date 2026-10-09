using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>Post 可选封面 HeadImage：引用改写、只认图片登记、以及 XSD 顺序。</summary>
[TestClass]
public class PostHeadImageTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    private static void SeedSchemas(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
            File.Copy(Path.Combine(RepoMetadataDir, name), Path.Combine(dir, name), overwrite: true);
    }

    [TestMethod]
    public void Build_PostHeadImage_QualifiesReferenceAndPublishes()
    {
        var temp = NewTemp();
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "news.md"), "# 公告\n\n正文内容足够长，用于通过校验。");
            File.WriteAllText(Path.Combine(src, "metadata.xml"),
                MetadataXml("<HeadImage>cover</HeadImage>", PostOrder.HeadImageBeforeContents));

            MetadataBuilder.Build(src, dst, contentRevision: "head-image");
            var post = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"))
                .Mods().Single(m => m.Id == "Demo").Raw
                .Find("Posts")!.Children.First(c => c.Name == "Post");

            post.Find("HeadImage")!.Value.Should().Be("metadata:cover");
        }
        finally
        {
            Cleanup(temp);
        }
    }

    [TestMethod]
    public void Build_PostHeadImage_RefersToMarkdown_Fails()
    {
        AssertRejected(PostOrder.HeadImageBeforeContents,
            "<HeadImage>news</HeadImage>", "*Post HeadImage*", "*news*");
    }

    [TestMethod]
    public void Build_PostHeadImage_UnknownId_Fails()
    {
        AssertRejected(PostOrder.HeadImageBeforeContents,
            "<HeadImage>no-such-image</HeadImage>", "*Post HeadImage*", "*no-such-image*");
    }

    [TestMethod]
    public void Build_PostHeadImage_AfterContents_FailsSchema()
    {
        AssertRejected(PostOrder.HeadImageAfterContents,
            "<HeadImage>cover</HeadImage>", "*XSD 校验失败*", "*HeadImage*");
    }

    private static void AssertRejected(PostOrder order, string headImage, params string[] messageParts)
    {
        var temp = NewTemp();
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "news.md"), "# 公告\n\n正文内容足够长，用于通过校验。");
            File.WriteAllText(Path.Combine(src, "metadata.xml"), MetadataXml(headImage, order));

            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "head-image");
            var assertion = act.Should().Throw<InvalidOperationException>();
            foreach (var part in messageParts)
                assertion.WithMessage(part);
        }
        finally
        {
            Cleanup(temp);
        }
    }

    private enum PostOrder
    {
        HeadImageBeforeContents,
        HeadImageAfterContents,
    }

    private static string MetadataXml(string headImage, PostOrder order)
    {
        var before = order == PostOrder.HeadImageBeforeContents ? $"        {headImage}\n" : string.Empty;
        var after = order == PostOrder.HeadImageAfterContents ? $"        {headImage}\n" : string.Empty;

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="cover" Url="https://example.com/cover.jpg" />
  <Markdown ID="news" Source="news.md" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示模组</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Posts>
      <Post DateTime="2026-01-01T00:00:00">
        <Titles>
          <Title Language="zh">公告</Title>
        </Titles>
{before}        <Contents>
          <Content Language="zh">news</Content>
        </Contents>
{after}      </Post>
    </Posts>
  </Mod>
</Metadata>
""";
    }

    private static string NewTemp() => Path.Combine(Path.GetTempPath(), $"head-image-{Guid.NewGuid():N}");

    private static void Cleanup(string temp)
    {
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
    }
}
