using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>Post 可选类型 @Type：源与发布两份 XSD 都必须接受省略与任意字符串取值。</summary>
[TestClass]
public class PostTypeTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    [TestMethod]
    [DataRow("", "省略 Type 仍合法，等同于普通新闻")]
    [DataRow(" Type=\"introduction\"", "introduction 用途合法")]
    [DataRow(" Type=\"Changelog\"", "Changelog 用途合法")]
    [DataRow(" Type=\"custom-unknown\"", "不限制取值：未知字符串同样合法")]
    [DataRow(" Type=\"\"", "xs:string 允许空串，模式不做非空约束")]
    public void SourceAndPublishSchema_PostType_OptionalAndAnyString(string typeAttribute, string because)
    {
        var sourceSchema = Path.Combine(RepoMetadataDir, SchemaValidator.SourceSchemaFileName);
        var publishSchema = Path.Combine(RepoMetadataDir, SchemaValidator.PublishSchemaFileName);

        var temp = Path.Combine(Path.GetTempPath(), $"post-type-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var sourcePath = Path.Combine(temp, "source.xml");
            var publishPath = Path.Combine(temp, "publish.xml");
            File.WriteAllText(sourcePath, Document(typeAttribute, rootAttributes: string.Empty));
            File.WriteAllText(publishPath,
                Document(typeAttribute, rootAttributes: " SchemaVersion=\"1.0\" ContentRevision=\"post-type\""));

            SchemaValidator.ValidateFile(sourcePath, sourceSchema).Should().BeEmpty(because);
            SchemaValidator.ValidateFile(publishPath, publishSchema).Should().BeEmpty(because);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>同一份 Post 树分别用作源文件（根无版本属性）与发布物（根带 SchemaVersion/ContentRevision）。</summary>
    private static string Document(string typeAttribute, string rootAttributes) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<Metadata{rootAttributes}>
  <Markdown ID="news" Source="news.md" />
  <Mod ID="Demo">
    <DisplayName Language="zh">演示模组</DisplayName>
    <DisplayName Language="en">Demo Mod</DisplayName>
    <Posts>
      <Post{typeAttribute} DateTime="2026-01-01T00:00:00">
        <Titles>
          <Title Language="zh">标题</Title>
        </Titles>
        <Contents>
          <Content Language="zh">news</Content>
        </Contents>
      </Post>
    </Posts>
  </Mod>
</Metadata>
""";
}
